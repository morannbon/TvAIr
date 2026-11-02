// 日本の祝日（国民の祝日、振替休日、国民の休日）。番組表の日付表示専用。
// 2000～2099年の春分・秋分は国立天文台の近似式に基づく。法改正時はここを更新する。
(function (global) {
  'use strict';
  const cache = new Map();
  const pad = n => String(n).padStart(2, '0');
  const key = (y, m, d) => `${y}-${pad(m)}-${pad(d)}`;
  const utc = (y, m, d) => Date.UTC(y, m - 1, d);
  const weekday = (y, m, d) => new Date(utc(y, m, d)).getUTCDay();
  const nthMonday = (y, m, nth) => 1 + ((8 - weekday(y, m, 1)) % 7) + 7 * (nth - 1);
  const equinox = (y, autumn) => Math.floor((autumn ? 23.2488 : 20.69115)
    + 0.242194 * (y - 2000) - Math.floor((y - 2000) / 4));

  function holidaysOfYear(y) {
    if (cache.has(y)) return cache.get(y);
    const holidays = new Set();
    const add = (m, d) => holidays.add(key(y, m, d));
    if (y < 2000 || y > 2099) { cache.set(y, holidays); return holidays; }

    add(1, 1); // 元日
    add(1, nthMonday(y, 1, 2)); // 成人の日
    add(2, 11); // 建国記念の日
    if (y >= 2020) add(2, 23); // 天皇誕生日
    add(3, equinox(y, false)); // 春分の日
    add(4, 29); add(5, 3); add(5, 4); add(5, 5);
    if (y >= 2003) add(7, nthMonday(y, 7, 3)); else add(7, 20); // 海の日
    if (y >= 2016) add(8, 11); // 山の日
    add(9, nthMonday(y, 9, 3)); // 敬老の日
    add(9, equinox(y, true)); // 秋分の日
    add(10, nthMonday(y, 10, 2)); // スポーツの日
    add(11, 3); add(11, 23);
    if (y <= 2018) add(12, 23); // 旧天皇誕生日

    // 改元および東京五輪開催に伴う祝日特例。
    if (y === 2019) { add(5, 1); add(10, 22); }
    if (y === 2020 || y === 2021) {
      holidays.delete(key(y, 7, nthMonday(y, 7, 3)));
      holidays.delete(key(y, 10, nthMonday(y, 10, 2)));
      holidays.delete(key(y, 8, 11));
      add(7, y === 2020 ? 23 : 22);
      add(7, y === 2020 ? 24 : 23);
      add(8, y === 2020 ? 10 : 8);
    }

    // 祝日に挟まれた平日は国民の休日。
    const allDays = [];
    for (let m = 1; m <= 12; m++) {
      const count = new Date(utc(y, m + 1, 0)).getUTCDate();
      for (let d = 1; d <= count; d++) allDays.push(key(y, m, d));
    }
    for (let i = 1; i < allDays.length - 1; i++) {
      if (!holidays.has(allDays[i]) && holidays.has(allDays[i - 1]) && holidays.has(allDays[i + 1])) {
        holidays.add(allDays[i]);
      }
    }
    // 日曜に重なった祝日の、次の休日でない日を振替休日とする。
    const originals = Array.from(holidays).sort();
    for (const date of originals) {
      const [yy, mm, dd] = date.split('-').map(Number);
      if (weekday(yy, mm, dd) !== 0) continue;
      let next = utc(yy, mm, dd) + 86400000;
      while (true) {
        const d = new Date(next);
        const target = key(d.getUTCFullYear(), d.getUTCMonth() + 1, d.getUTCDate());
        if (!holidays.has(target)) { if (d.getUTCFullYear() === y) holidays.add(target); break; }
        next += 86400000;
      }
    }
    cache.set(y, holidays);
    return holidays;
  }

  global.TvAIrJapaneseHolidays = Object.freeze({
    has(dateString) {
      if (typeof dateString !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(dateString)) return false;
      return holidaysOfYear(Number(dateString.slice(0, 4))).has(dateString);
    }
  });
})(window);
