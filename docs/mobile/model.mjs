export const marker = "route-live-2c57";

export function interrupt(entries) {
  const last = entries.at(-1);
  return last && last.end === null ? [...entries.slice(0,-1), {...last,end:last.seen,interrupted:true}] : entries;
}

export function observe(entries, reachable, time, previous) {
  const gap = previous === null || time < previous || time - previous > 55_000;
  const history = (gap ? interrupt(entries) : entries).slice();
  const current = history.at(-1);
  if (current && current.end === null) {
    history[history.length - 1] = reachable ? {...current, end: time} : {...current, seen: time};
  } else if (!reachable) {
    history.push({start: time, seen: time, end: null, interrupted: false});
  }
  return history.slice(-200);
}

export function duration(start, end) {
  const seconds = Math.max(0, Math.floor((end - start) / 1000));
  const pad = value => String(value).padStart(2, "0");
  const h = Math.floor(seconds / 3600);
  return (h ? h + ":" : "") + pad(Math.floor(seconds / 60) % 60) + ":" + pad(seconds % 60);
}

export function calendar(dateValue, lead, now = new Date()) {
  const [y,m,d] = dateValue.split("-").map(Number);
  if (!y || !m || !d || ![0,1,3,7,14].includes(lead)) throw new Error("Неверная дата");
  const day = new Date(Date.UTC(y,m-1,d));
  if (day.getUTCFullYear() !== y || day.getUTCMonth() !== m-1 || day.getUTCDate() !== d) throw new Error("Неверная дата");
  const basic = dt => dt.toISOString().replace(/[-:]/g,"").slice(0,8);
  const stamp = now.toISOString().replace(/[-:]/g,"").replace(/\.\d{3}Z$/,"Z");
  // Use a floating 09:00 start so the event follows the iPhone's local timezone.
  return [
    "BEGIN:VCALENDAR","VERSION:2.0","PRODID:-//Route//Internet Payment//RU","CALSCALE:GREGORIAN",
    "BEGIN:VEVENT","UID:internet-payment-"+basic(day)+"@internet-again-route",
    "DTSTAMP:"+stamp,"DTSTART:"+basic(day)+"T090000","DTEND:"+basic(day)+"T091000",
    "SUMMARY:Оплата интернета","BEGIN:VALARM","ACTION:DISPLAY",
    "DESCRIPTION:Оплата интернета","TRIGGER:"+(lead ? "-P"+lead+"D" : "PT0M"),
    "END:VALARM","END:VEVENT","END:VCALENDAR","",
  ].join("\r\n");
}
