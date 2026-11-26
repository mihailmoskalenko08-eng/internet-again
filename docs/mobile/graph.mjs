const clamp = (value, min, max) => Math.max(min, Math.min(max, value));

export function floatGraph(nodes, links, seconds, delta) {
  const dt = clamp(delta, 0, .032);
  const forces = nodes.map(node => ({
    x: (node.homeX + 5 * Math.sin(seconds * .75 + node.phase) - node.x) * 5,
    y: (node.homeY + 4 * Math.cos(seconds * .61 + node.phase) - node.y) * 5,
  }));

  for (const {from, to, length} of links) {
    const dx = nodes[to].x - nodes[from].x;
    const dy = nodes[to].y - nodes[from].y;
    const distance = Math.hypot(dx, dy) || 1;
    const pull = (distance - length) * 1.1 / distance;
    forces[from].x += dx * pull;
    forces[from].y += dy * pull;
    forces[to].x -= dx * pull;
    forces[to].y -= dy * pull;
  }

  for (let from = 0; from < nodes.length; from++) {
    for (let to = from + 1; to < nodes.length; to++) {
      let dx = nodes[to].x - nodes[from].x;
      let dy = nodes[to].y - nodes[from].y;
      if (dx === 0 && dy === 0) {
        dx = .01;
        dy = .02;
      }
      const distance = Math.hypot(dx, dy);
      if (distance >= 25) continue;
      const push = (25 - distance) * 5 / distance;
      forces[from].x -= dx * push;
      forces[from].y -= dy * push;
      forces[to].x += dx * push;
      forces[to].y += dy * push;
    }
  }

  const damping = Math.exp(-3.7 * dt);
  nodes.forEach((node, index) => {
    if (node.held) return;
    const {x, y} = forces[index];
    node.vx = clamp((node.vx + x * dt) * damping,-45,45);
    node.vy = clamp((node.vy + y * dt) * damping,-45,45);
    node.x = clamp(node.x + node.vx * dt, 13, 87);
    node.y = clamp(node.y + node.vy * dt, 10, 84);
  });
}

export function attachGraph(graph, reducedMotion) {
  const buttons = Array.from(graph.querySelectorAll(".node"));
  const nodes = buttons.map((button,index) => {
    const homeX = parseFloat(button.style.getPropertyValue("--x"));
    const homeY = parseFloat(button.style.getPropertyValue("--y"));
    return {homeX,homeY,x:homeX,y:homeY,phase:index*1.9,vx:0,vy:0,held:false};
  });
  const indexOf = id => buttons.findIndex(button => button.dataset.node === id);
  const links = Array.from(graph.querySelectorAll("[data-edge]")).map(line => {
    const [from,to] = line.dataset.edge.split(" ").map(indexOf);
    const length = Math.hypot(nodes[from].x-nodes[to].x,nodes[from].y-nodes[to].y);
    return {line,from,to,length};
  });
  const signal = graph.querySelector(".signal");
  const route = ["device","request","answer"].map(id => nodes[indexOf(id)]);
  let frame = 0, lastFrame = 0, signalStarted = null;
  let visible = false, pointer = null, suppressClick = false;

  function draw(seconds) {
    nodes.forEach(({x,y},index) => {
      buttons[index].style.setProperty("--x",x+"%");
      buttons[index].style.setProperty("--y",y+"%");
    });
    for (const {line,from,to} of links) {
      const a=nodes[from], b=nodes[to];
      line.setAttribute("x1",a.x);
      line.setAttribute("y1",a.y);
      line.setAttribute("x2",b.x);
      line.setAttribute("y2",b.y);
    }
    if (graph.dataset.phase!=="checking" || reducedMotion.matches) {
      signalStarted=null;
      signal.style.opacity="0";
      return;
    }
    if (signalStarted===null) signalStarted=seconds;
    const progress=(Math.max(0,seconds-signalStarted) % .87)/.87;
    const leg=progress<.5?0:1;
    const p=progress*2-leg;
    const a=route[leg], b=route[leg+1];
    signal.style.left=(a.x+(b.x-a.x)*p)+"%";
    signal.style.top=(a.y+(b.y-a.y)*p)+"%";
    signal.style.opacity=String(Math.min(1,progress*10,(1-progress)*10));
  }

  function tick(now) {
    frame=0;
    if (!visible || reducedMotion.matches || document.hidden) return;
    floatGraph(nodes,links,now/1000,lastFrame?(now-lastFrame)/1000:0);
    lastFrame=now;
    draw(now/1000);
    frame=requestAnimationFrame(tick);
  }

  function refresh() {
    if (frame) cancelAnimationFrame(frame);
    frame=0;
    lastFrame=0;
    signalStarted=null;
    if (visible && !reducedMotion.matches && !document.hidden) frame=requestAnimationFrame(tick);
    else signal.style.opacity="0";
  }

  function release(event) {
    if (!pointer || (event && pointer.id!==event.pointerId)) return;
    pointer.node.held=false;
    if (!pointer.moved || reducedMotion.matches || event?.type==="pointercancel") {
      pointer.node.vx=0;
      pointer.node.vy=0;
    }
    pointer.button.classList.remove("dragging");
    pointer=null;
  }

  buttons.forEach((button,index) => {
    button.addEventListener("pointerdown",event => {
      if (pointer || event.button!==0) return;
      suppressClick=false;
      const node=nodes[index];
      node.held=true;
      node.vx=node.vy=0;
      pointer={id:event.pointerId,button,node,x:node.x,y:node.y,clientX:event.clientX,clientY:event.clientY,
        time:event.timeStamp,moved:false};
      button.setPointerCapture(event.pointerId);
    });
    button.addEventListener("pointermove",event => {
      if (pointer?.id!==event.pointerId) return;
      const dx=event.clientX-pointer.clientX, dy=event.clientY-pointer.clientY;
      if (!pointer.moved && Math.hypot(dx,dy)<5) return;
      const {width,height}=graph.getBoundingClientRect();
      if (!width || !height) return;
      pointer.moved=suppressClick=true;
      button.classList.add("dragging");
      const node=pointer.node;
      const x=clamp(pointer.x+dx/width*100,13,87);
      const y=clamp(pointer.y+dy/height*100,10,84);
      const delta=Math.max(.016,(event.timeStamp-pointer.time)/1000);
      node.vx=clamp((x-node.x)/delta,-40,40);
      node.vy=clamp((y-node.y)/delta,-40,40);
      node.x=x;
      node.y=y;
      pointer.time=event.timeStamp;
      draw(performance.now()/1000);
    });
    for (const event of ["pointerup","pointercancel","lostpointercapture"])
      button.addEventListener(event,release);
  });
  graph.addEventListener("click",event => {
    if (!suppressClick || event.detail===0) return;
    suppressClick=false;
    event.preventDefault();
    event.stopImmediatePropagation();
  },true);
  reducedMotion.addEventListener("change",refresh);
  document.addEventListener("visibilitychange",() => {
    if (document.hidden) release();
    refresh();
  });

  return {
    setVisible(value) {
      if (value===visible) return;
      visible=value;
      if (!visible) release();
      refresh();
    },
    select(id) {
      const selected=indexOf(id);
      for (const {line,from,to} of links) line.classList.toggle("connected",from===selected || to===selected);
    },
  };
}
