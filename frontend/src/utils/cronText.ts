const WEEKDAYS = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

/** A plain number from `min` to `max`, or null for anything else. */
function value(field: string, min: number, max: number): number | null {
  if (!/^\d{1,2}$/.test(field)) return null;
  const n = Number(field);
  return n >= min && n <= max ? n : null;
}

const twoDigits = (n: number) => String(n).padStart(2, "0");

/**
 * A five-field cron in plain words, for the hourly, daily and weekly shapes the
 * schedule presets make: "Every hour at minute 15", "Every day at 09:30",
 * "Every Monday at 08:00". Null for every other shape, which is shown as is.
 */
export function cronText(expression: string): string | null {
  const fields = expression.trim().split(/\s+/);
  if (fields.length !== 5) return null;
  const [minuteField, hourField, dayField, monthField, weekdayField] = fields;
  const minute = value(minuteField, 0, 59);
  if (minute === null || dayField !== "*" || monthField !== "*") return null;

  if (hourField === "*") {
    if (weekdayField !== "*") return null;
    return minute === 0 ? "Every hour, on the hour" : `Every hour at minute ${minute}`;
  }

  const hour = value(hourField, 0, 23);
  if (hour === null) return null;
  const time = `${twoDigits(hour)}:${twoDigits(minute)}`;
  if (weekdayField === "*") return `Every day at ${time}`;

  const weekday = value(weekdayField, 0, 7);
  return weekday === null ? null : `Every ${WEEKDAYS[weekday % 7]} at ${time}`;
}
