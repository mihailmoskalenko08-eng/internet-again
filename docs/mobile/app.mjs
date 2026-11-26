import {calendar, duration, interrupt, marker, observe} from "./model.mjs";
import {attachGraph} from "./graph.mjs";

const $ = id => document.getElementById(id);
const tabList = document.querySelector(".navbar");
const indicator = tabList.querySelector(".nav-indicator");
const tabs = Array.from(tabList.querySelectorAll('[role="tab"]'));
const panels = tabs.map(tab => $(tab.getAttribute("aria-controls")));
const reducedMotion = matchMedia("(prefers-reduced-motion: reduce)");
const graph = attachGraph($("graph"), reducedMotion);
let selectedTab = 0;
let animations = [];
function settle() {
  for (const animation of animations) animation.cancel();
  animations = [];
  panels.forEach((panel,index) => {
    panel.hidden = index !== selectedTab;
    panel.inert = false;
    panel.classList.remove("leaving");
  });
  tabList.style.setProperty("--selected",selectedTab);
}
function selectTab(next) {
  if (next === selectedTab) return;
  settle();
  const previous = selectedTab;
  const direction = next > previous ? 1 : -1;
  selectedTab = next;
  graph.setVisible(panels[next].id==="map");
  tabs.forEach((tab,index) => {
    tab.setAttribute("aria-selected", index === next);
    tab.tabIndex = index === next ? 0 : -1;
  });
  if (window.scrollY > 0) window.scrollTo(0,0);
  if (reducedMotion.matches || typeof indicator.animate !== "function") return settle();
  const outgoing = panels[previous], incoming = panels[next];
  outgoing.classList.add("leaving");
  outgoing.inert = true;
  incoming.hidden = false;
  const options = {duration:320,easing:"cubic-bezier(.22,.66,.33,1)",fill:"both"};
  const translate = distance => "translateX("+distance+"%)";
  const entry = incoming.animate([{transform:translate(direction*100)},{transform:translate(0)}],options);
  const exit = outgoing.animate([{transform:translate(0)},{transform:translate(-direction*100)}],options);
  const rail = indicator.animate([
    {transform:translate(previous*100)+" scaleX(1)"},
    {transform:translate((previous+next)*50)+" scaleX(.65)",offset:.46},
    {transform:translate(next*100)+" scaleX(1)"},
  ],options);
  animations = [entry,exit,rail];
  Promise.all(animations.map(animation => animation.finished)).then(() => {
    if (animations.includes(entry)) settle();
  }, () => {});
}
tabs.forEach((tab,index) => {
  tab.addEventListener("click", () => selectTab(index));
  tab.addEventListener("keydown", event => {
    const targets={ArrowLeft:index-1,ArrowRight:index+1,Home:0,End:tabs.length-1};
    if (!(event.key in targets)) return;
    event.preventDefault();
    const next=(targets[event.key]+tabs.length)%tabs.length;
    selectTab(next);
    tabs[next].focus();
  });
});

const prefix="route-mobile-v1:";
function load(key,fallback) {
  try { return JSON.parse(localStorage.getItem(prefix+key)) ?? fallback; }
  catch { return fallback; }
}
function store(key,value) {
  try { localStorage.setItem(prefix+key,JSON.stringify(value)); return true; }
  catch { return false; }
}
let history=load("history",[]);
history=Array.isArray(history)?history.filter(item => item && Number.isFinite(item.start) && Number.isFinite(item.seen) && (item.end===null || Number.isFinite(item.end))):[];
let previous=null;
let phase="checking";
let measure=null;
let checkedAt=null;
let pending=false;
let offlineState="unavailable";
let selectedNode="request";
const fullDate=new Intl.DateTimeFormat("ru-RU",{day:"numeric",month:"long",hour:"2-digit",minute:"2-digit"});
const timeOnly=new Intl.DateTimeFormat("ru-RU",{hour:"2-digit",minute:"2-digit",second:"2-digit"});
const localTime = value => timeOnly.format(value);
const active = () => history.at(-1)?.end === null ? history.at(-1) : null;

function renderCheck() {
  const current=active();
  const words=phase==="online"?["СВЯЗЬ","РАБОТАЕТ"]:phase==="offline"?["НЕТ ОТВЕТА","ОТ САЙТА"]:["ПРОВЕРЯЕМ","СВЯЗЬ"];
  $("status").replaceChildren(words[0],document.createElement("br"),words[1]);
  $("route-detail").textContent=phase==="online"?"Контрольный сайт ответил · "+measure+" мс":
    phase==="offline"?"Контрольный сайт не ответил":"Отправляем запрос";
  $("device-status").textContent=navigator.onLine?"Тип подключения не определён":"Устройство сообщает, что сети нет";
  $("route-result").dataset.phase=phase;
  $("graph").dataset.phase=pending?"checking":phase;
  $("last-check").textContent=pending?"Проверяем ответ сайта…":checkedAt!==null?"Последняя проверка · "+localTime(checkedAt):"Первая проверка";
  $("outage-start").textContent=current?"Обнаружен в "+localTime(current.start):checkedAt!==null?"Активного сбоя нет":"Ожидаем результат";
  $("check").disabled=$("map-check").disabled=pending;
  $("check").textContent=pending?"Проверяем…":"Проверить сейчас";
  $("map-check").textContent=pending?"Запрос в пути…":"Запустить проверку";
  renderClock();
  renderNode();
}
function renderClock() {
  const current=active();
  $("duration").textContent=current?duration(current.start,Math.min(Date.now(),current.seen+55_000)):"—";
  const clock=$("history").querySelector("li.live strong");
  if (clock && current) clock.textContent=$("duration").textContent;
}
function renderHistory() {
  const list=$("history");
  list.replaceChildren();
  $("export").disabled=history.length===0;
  if (!history.length) {
    const item=document.createElement("li");
    item.className="empty";
    item.textContent="Сбоев пока нет.";
    list.append(item);
    return;
  }
  for (const entry of history.slice().reverse()) {
    const item=document.createElement("li");
    const current=entry.end===null;
    if (current) item.className="live";
    const info=document.createElement("div");
    const when=document.createElement("time");
    when.dateTime=new Date(entry.start).toISOString();
    when.textContent=fullDate.format(entry.start);
    const label=document.createElement("b");
    label.textContent=current?"Сбой продолжается":entry.interrupted?"Наблюдение прервано":"Сайт снова отвечает";
    const elapsed=document.createElement("strong");
    elapsed.textContent=duration(entry.start,entry.end??Date.now());
    info.append(when,label);
    item.append(info,elapsed);
    list.append(item);
  }
}
const nodes={
  device:()=>["Устройство",navigator.onLine?"Сеть может быть доступна":"Устройство без сети","Safari не определяет тип сети и не показывает, отвечает ли роутер."],
  request:()=>["Запрос",pending?"Запрос в пути":"Проверка ответа","Отправляем свежий HTTPS-запрос к этому сайту, минуя офлайн-кэш."],
  answer:()=>["Ответ",phase==="online"?"Получен за "+measure+" мс":phase==="offline"?"Ответа нет":"Ждём ответ",phase==="offline"?"Сайт проверки не ответил. Причина сбоя пока неизвестна.":"Проверяем ответ именно этого сервиса."],
  cache:()=>["Офлайн",offlineState==="ready"?"Интерфейс сохранён":offlineState==="preparing"?"Подготовка офлайн-доступа":"Офлайн-доступ недоступен","После успешного сохранения интерфейс может открываться без сети."],
  journal:()=>["Журнал",history.length?"Записей: "+history.length:"Сбоев пока нет","Проверки выполняются, пока приложение открыто. Записи хранятся в этом браузере."],
};
function renderNode() {
  const [label,title,detail]=nodes[selectedNode]();
  $("node-label").textContent=label;
  $("node-title").textContent=title;
  $("node-info").textContent=detail;
}
for (const node of document.querySelectorAll(".node")) {
  node.addEventListener("click", () => {
    selectedNode=node.dataset.node;
    graph.select(selectedNode);
    for (const button of document.querySelectorAll(".node")) button.setAttribute("aria-pressed",button===node);
    renderNode();
  });
}
async function probe() {
  if (pending || document.hidden) return;
  pending=true;
  renderCheck();
  const controller=new AbortController();
  const deadline=setTimeout(() => controller.abort(),4500);
  const started=performance.now();
  let reachable=false;
  try {
    const url=new URL("./probe.txt",location.href);
    url.searchParams.set("t",String(Date.now()));
    const response=await fetch(url,{cache:"no-store",redirect:"error",signal:controller.signal});
    reachable=response.ok && (await response.text()).trim()===marker;
  } catch {
    reachable=false;
  } finally {
    clearTimeout(deadline);
  }
  measure=Math.round(performance.now()-started);
  if (!reducedMotion.matches && !panels[1].hidden && measure < 900)
    await new Promise(resolve => setTimeout(resolve,900-measure));
  const now=Date.now();
  pending=false;
  if (document.hidden) return;
  history=observe(history,reachable,now,previous);
  previous=now;
  checkedAt=now;
  phase=reachable?"online":"offline";
  store("history",history);
  renderHistory();
  renderCheck();
}
$("check").addEventListener("click",probe);
$("map-check").addEventListener("click",probe);
window.addEventListener("online",probe);
window.addEventListener("offline",probe);
function pause() {
  previous=null;
  const updated=interrupt(history);
  if (updated!==history) {
    history=updated;
    store("history",history);
    renderHistory();
    renderCheck();
  }
}
document.addEventListener("visibilitychange",() => document.hidden ? pause() : probe());
window.addEventListener("pagehide",pause);
setInterval(probe,20_000);
setInterval(renderClock,1000);

function download(text,name,mediaType) {
  const url=URL.createObjectURL(new Blob([text],{type:mediaType}));
  const link=document.createElement("a");
  link.href=url;
  link.download=name;
  document.body.append(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url),60_000);
}
$("export").addEventListener("click",() => {
  const lines=history.map(e => fullDate.format(e.start)+" | "+duration(e.start,e.end??Date.now())+" | "+
    (e.end===null?"сбой продолжается":e.interrupted?"перерыв наблюдения":"сайт снова отвечает"));
  download("Журнал сбоев · Маршрут\r\n"+lines.join("\r\n"),"route-report.txt","text/plain;charset=utf-8");
});
const payment=load("payment",{});
$("due").value=typeof payment?.date==="string"?payment.date:"";
if ([0,1,3,7,14].includes(payment?.lead)) $("lead").value=String(payment.lead);
function renderPayment() {
  const value=$("due").value;
  $("payment-date").textContent=value?value.split("-").reverse().join("."):"—";
}
$("due").addEventListener("input",renderPayment);
$("payment-form").addEventListener("submit",event => {
  event.preventDefault();
  const date=$("due").value;
  const lead=Number($("lead").value);
  const feedback=$("calendar-feedback");
  try {
    const reminder=calendar(date,lead);
    const saved=store("payment",{date,lead});
    download(reminder,"internet-payment.ics","text/calendar;charset=utf-8");
    feedback.textContent=saved?"Файл готов. Открой его и добавь событие в календарь.":"Файл готов. Этот браузер не сохранил дату в приложении.";
  } catch {
    feedback.textContent="Проверь дату оплаты и срок напоминания.";
  }
  feedback.hidden=false;
});
graph.select(selectedNode);
renderPayment();
renderHistory();
renderCheck();
probe();
if ("serviceWorker" in navigator) {
  offlineState="preparing";
  renderNode();
  navigator.serviceWorker.register("./sw.js").then(() => navigator.serviceWorker.ready).then(() => {
    offlineState="ready";
    renderNode();
  }).catch(() => {
    offlineState="unavailable";
    renderNode();
  });
}
