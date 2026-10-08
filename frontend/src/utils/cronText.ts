const WEEKDAYS = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

/** The crons a schedule's Hourly, Daily and Weekly presets fill in. */
export const CRON_PRESETS = [
  { label: "Hourly", cron: "0 * * * *" },
  { label: "Daily", cron: "0 9 * * *" },
  { label: "Weekly", cron: "0 9 * * 1" },
] as const;

/** A plain number from `min` to `max`, or null for anything else. */
function plainNumber(field: string, min: number, max: number): number | null {
  if (!/^\d{1,2}$/.test(field)) return null;
  const n = Number(field);
  return n >= min && n <= max ? n : null;
}

const twoDigits = (n: number) => String(n).padStart(2, "0");

/**
 * A five-field cron in plain words, for the shapes the presets make: "Every hour
 * at minute 15", "Every day at 09:30", "Every Monday at 08:00". Any other shape
 * comes back as the cron itself.
 */
export function describeCron(expression: string): string {
  const fields = expression.trim().split(/\s+/);
  if (fields.length !== 5) return expression;
  const [minuteField, hourField, dayField, monthField, weekdayField] = fields;
  const minute = plainNumber(minuteField, 0, 59);
  if (minute === null || dayField !== "*" || monthField !== "*") return expression;

  if (hourField === "*") {
    if (weekdayField !== "*") return expression;
    return minute === 0 ? "Every hour, on the hour" : `Every hour at minute ${minute}`;
  }

  const hour = plainNumber(hourField, 0, 23);
  if (hour === null) return expression;
  const time = `${twoDigits(hour)}:${twoDigits(minute)}`;
  if (weekdayField === "*") return `Every day at ${time}`;

  const weekday = plainNumber(weekdayField, 0, 7);
  return weekday === null ? expression : `Every ${WEEKDAYS[weekday % 7]} at ${time}`;
}
