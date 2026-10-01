namespace ZapretSmart.Core.Share;

/// <summary>
/// Страница /i: перед профилем iPhone короткий ролик Zapret Smart, обыгранный как кинопоказ. Видео идёт без звука
/// (iOS иначе не запустит его сам), звук включается кнопкой. В режиме энергосбережения iOS не запускает видео вовсе:
/// тогда появляется кнопка «Смотреть». Профиль можно забрать сразу, а после ролика он загружается сам. Если видео
/// не загрузилось, страница не держит человека и отдаёт профиль.
/// </summary>
public static class IntroPage
{
    public const string VideoPath = "/intro.mp4";
    public const string ProfilePath = "/i.mobileconfig";

    public const string Html = """
        <!doctype html>
        <html lang="ru">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
        <title>Zapret Smart: раздача</title>
        <style>
          :root { color-scheme: dark; }
          html, body { margin: 0; height: 100%; background: #000; color: #f4f4f5;
            font: 16px/1.45 -apple-system, BlinkMacSystemFont, "Segoe UI", system-ui, sans-serif; }
          .stage { position: fixed; inset: 0; background: #000; }
          video { width: 100%; height: 100%; object-fit: contain; background: #000; display: block; }
          .top { position: fixed; top: 0; left: 0; right: 0; padding: calc(12px + env(safe-area-inset-top)) 16px 28px;
            display: flex; align-items: center; justify-content: space-between; gap: 10px;
            background: linear-gradient(#000d, #0000); }
          .tag { font-size: 13px; letter-spacing: .02em; opacity: .9; }
          .tag b { display: block; font-size: 15px; letter-spacing: 0; }
          .bottom { position: fixed; left: 0; right: 0; bottom: calc(18px + env(safe-area-inset-bottom));
            display: flex; justify-content: center; gap: 10px; padding: 0 16px; }
          .btn { appearance: none; border: 0; border-radius: 999px; padding: 11px 18px; cursor: pointer;
            font: 600 15px -apple-system, system-ui, sans-serif; color: #000; background: #fff; text-decoration: none; }
          .ghost { background: #ffffff2e; color: #fff; -webkit-backdrop-filter: blur(10px); backdrop-filter: blur(10px); }
          .bar { position: fixed; left: 0; right: 0; bottom: 0; height: 4px; background: #ffffff1f; }
          .bar i { display: block; height: 100%; width: 0; background: #6ee7f9; transition: width .25s linear; }
          .final { position: fixed; inset: 0; display: none; flex-direction: column; align-items: center; justify-content: center;
            gap: 12px; padding: 24px 20px calc(24px + env(safe-area-inset-bottom)); text-align: center;
            background: radial-gradient(120% 80% at 50% 0%, #0e3a46 0%, #050608 60%); }
          .final.show { display: flex; }
          .final h1 { margin: 0; font-size: 30px; letter-spacing: -.01em; }
          .final ol { margin: 4px 0 8px; padding-left: 22px; text-align: left; max-width: 340px; }
          .final li { margin: 6px 0; }
          .note { font-size: 13px; opacity: .7; max-width: 340px; margin: 6px 0 0; }
        </style>
        </head>
        <body>
        <div class="stage"><video id="v" src="/intro.mp4" playsinline muted autoplay preload="auto"></video></div>
        <div class="top">
          <span class="tag"><b>Профиль готов.</b>Но сначала кино, 20 секунд.</span>
          <button class="btn ghost" id="sound" type="button">Со звуком</button>
        </div>
        <div class="bottom">
          <button class="btn ghost" id="tap" type="button" hidden>Смотреть</button>
          <a class="btn" id="skip" href="/i.mobileconfig">Сразу к профилю</a>
        </div>
        <div class="bar"><i id="p"></i></div>
        <section class="final" id="final">
          <h1>Финальные титры</h1>
          <p>Профиль загружается. Осталось три шага:</p>
          <ol>
            <li>Нажмите «Разрешить» в окне Safari.</li>
            <li>Откройте Настройки, сверху будет «Профиль загружен».</li>
            <li>Нажмите «Установить» и введите код телефона.</li>
          </ol>
          <a class="btn" href="/i.mobileconfig">Загрузить профиль ещё раз</a>
          <p class="note">Когда ПК выключен, телефон в этой сети ходит в интернет напрямую. Удалить профиль: Настройки, Основные, VPN и управление устройством.</p>
        </section>
        <script>
          const v = document.getElementById('v'), bar = document.getElementById('p'), fin = document.getElementById('final');
          const tap = document.getElementById('tap'), sound = document.getElementById('sound');
          let done = false, waitingForTap = false;
          function profile() {
            if (done) return;
            done = true;
            v.pause();
            fin.classList.add('show');
            location.href = '/i.mobileconfig';
          }
          v.addEventListener('timeupdate', () => { if (v.duration) bar.style.width = (v.currentTime / v.duration * 100) + '%'; });
          v.addEventListener('ended', profile);
          v.addEventListener('error', profile);
          document.getElementById('skip').addEventListener('click', e => { e.preventDefault(); profile(); });
          // Режим энергосбережения не даёт видео запуститься самому: тогда нужна кнопка.
          const started = v.play();
          if (started) started.catch(() => { waitingForTap = true; tap.hidden = false; });
          tap.addEventListener('click', () => { tap.hidden = true; waitingForTap = false; v.play(); });
          sound.addEventListener('click', () => {
            v.muted = !v.muted;
            sound.textContent = v.muted ? 'Со звуком' : 'Без звука';
            if (v.paused) v.play();
          });
          // Видео так и не загрузилось: не держим человека перед пустым экраном.
          setTimeout(() => { if (!done && !waitingForTap && v.readyState < 2) profile(); }, 8000);
        </script>
        </body>
        </html>
        """;

    /// <summary>
    /// Диапазон из заголовка Range для файла длиной length. null: отдать файл целиком (заголовка нет, он не про байты,
    /// диапазонов несколько или он кривой, так разрешает RFC 9110). Satisfiable=false: диапазон за концом файла, ответ 416.
    /// </summary>
    public static (long Start, long End, bool Satisfiable)? ParseRange(string? header, long length)
    {
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return null;
        var spec = header[6..].Trim();
        if (spec.Contains(',')) return null;
        var dash = spec.IndexOf('-');
        if (dash < 0) return null;
        var first = spec[..dash].Trim();
        var last = spec[(dash + 1)..].Trim();
        if (first.Length == 0)
        {
            // bytes=-N: последние N байт.
            if (!long.TryParse(last, out var suffix) || suffix <= 0) return null;
            return length == 0 ? (0, 0, false) : (Math.Max(0, length - suffix), length - 1, true);
        }
        if (!long.TryParse(first, out var start) || start < 0) return null;
        long end;
        if (last.Length == 0) end = length - 1;
        else if (!long.TryParse(last, out end) || end < start) return null;
        if (start >= length) return (start, start, false);
        return (start, Math.Min(end, length - 1), true);
    }
}
