namespace ZapretSmart.Core.Share;

/// <summary>
/// Страница /i: перед профилем iPhone короткий ролик Zapret Smart, обыгранный как кинопоказ. Сама ничего не запускает:
/// сначала «афиша» с кнопками «Смотреть со звуком», «Без звука» и «Сразу к профилю». Нажатие на кнопку считается действием
/// человека, и iOS разрешает видео со звуком; автозапуск iOS разрешает только без звука, и человек не успевал его
/// включить. Цвета страницы взяты из ролика (светлый фон, мятный акцент), поэтому она всегда светлая. После ролика
/// «Финальные титры» и загрузка профиля. Если видео не пошло, профиль отдаётся сразу.
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
        <meta name="color-scheme" content="light">
        <meta name="theme-color" content="#f5f4f7">
        <title>Zapret Smart: раздача</title>
        <style>
          :root { color-scheme: light; --bg: #f5f4f7; --ink: #111114; --muted: #6b6b76; --mint: #3cd0b0; --teal: #0b8868; --card: #ffffff; }
          html, body { margin: 0; height: 100%; background: var(--bg); color: var(--ink);
            font: 16px/1.45 -apple-system, BlinkMacSystemFont, "Segoe UI", system-ui, sans-serif; -webkit-font-smoothing: antialiased; }
          .stage { position: fixed; inset: 0; background: var(--bg); }
          video { width: 100%; height: 100%; object-fit: contain; background: var(--bg); display: block; }
          .logo { width: 64px; height: 64px; border-radius: 18px; background: #1b1d22; display: grid; place-items: center;
            box-shadow: 0 10px 30px #0b886833; }
          .logo i { width: 30px; height: 30px; border-radius: 50%; border: 7px solid var(--mint); box-sizing: border-box; position: relative; }
          .logo i::after { content: ""; position: absolute; inset: 4px; border-radius: 50%; background: #fff; }
          .btn { appearance: none; border: 0; border-radius: 999px; padding: 14px 22px; cursor: pointer; text-decoration: none;
            font: 600 16px -apple-system, system-ui, sans-serif; color: #fff; background: var(--ink); text-align: center; }
          .btn.light { background: var(--card); color: var(--ink); box-shadow: inset 0 0 0 1.5px #11111422; }
          .btn.small { padding: 10px 16px; font-size: 14px; }
          .link { color: var(--teal); font-weight: 600; text-decoration: none; padding: 8px; }
          .poster, .final { position: fixed; inset: 0; display: flex; flex-direction: column; align-items: center; justify-content: center;
            gap: 14px; padding: calc(24px + env(safe-area-inset-top)) 22px calc(24px + env(safe-area-inset-bottom)); text-align: center;
            background: radial-gradient(120% 70% at 50% 0%, #3cd0b026 0%, var(--bg) 60%); }
          .poster h1, .final h1 { margin: 6px 0 0; font-size: 30px; line-height: 1.15; letter-spacing: -.02em; }
          .poster p, .final p { margin: 0; color: var(--muted); max-width: 330px; }
          .actions { display: flex; flex-direction: column; gap: 10px; width: 100%; max-width: 320px; margin-top: 10px; }
          .hidden { display: none !important; }
          .top { position: fixed; top: 0; left: 0; right: 0; padding: calc(10px + env(safe-area-inset-top)) 14px 24px;
            display: flex; align-items: center; justify-content: space-between; gap: 10px;
            background: linear-gradient(var(--bg) 30%, #f5f4f700); }
          .tag { font-size: 13px; color: var(--muted); }
          .tag b { display: block; font-size: 15px; color: var(--ink); }
          .bottom { position: fixed; left: 0; right: 0; bottom: calc(16px + env(safe-area-inset-bottom)); display: flex; justify-content: center; }
          .bar { position: fixed; left: 0; right: 0; bottom: 0; height: 4px; background: #11111414; }
          .bar i { display: block; height: 100%; width: 0; background: var(--mint); transition: width .25s linear; }
          .final ol { margin: 4px 0; padding-left: 22px; text-align: left; max-width: 330px; }
          .final li { margin: 7px 0; }
          .note { font-size: 13px; }
        </style>
        </head>
        <body>
        <div class="stage"><video id="v" src="/intro.mp4#t=0.1" playsinline preload="auto"></video></div>

        <section class="poster" id="poster">
          <div class="logo"><i></i></div>
          <h1>Профиль готов.<br>Сначала кино, 20&nbsp;секунд.</h1>
          <p>Как Zapret Smart открывает заблокированное и раздаёт это на телефон.</p>
          <div class="actions">
            <button class="btn" id="withSound" type="button">Смотреть со звуком</button>
            <button class="btn light" id="noSound" type="button">Без звука</button>
            <a class="link" id="skipPoster" href="/i.mobileconfig">Сразу к профилю</a>
          </div>
        </section>

        <div class="top hidden" id="top">
          <span class="tag"><b>Zapret Smart</b>Профиль следом за титрами</span>
          <button class="btn light small" id="sound" type="button">Без звука</button>
        </div>
        <div class="bottom hidden" id="bottom"><a class="btn small" id="skip" href="/i.mobileconfig">Сразу к профилю</a></div>
        <div class="bar hidden" id="barBox"><i id="p"></i></div>

        <section class="final hidden" id="final">
          <div class="logo"><i></i></div>
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
          const $ = id => document.getElementById(id);
          const v = $('v'), sound = $('sound');
          let done = false;
          function profile() {
            if (done) return;
            done = true;
            v.pause();
            ['poster', 'top', 'bottom', 'barBox'].forEach(id => $(id).classList.add('hidden'));
            $('final').classList.remove('hidden');
            location.href = '/i.mobileconfig';
          }
          function setSoundLabel() { sound.textContent = v.muted ? 'Со звуком' : 'Без звука'; }
          // Видео запускается только по нажатию: это действие человека, и iOS разрешает звук сразу.
          function watch(withSound) {
            $('poster').classList.add('hidden');
            ['top', 'bottom', 'barBox'].forEach(id => $(id).classList.remove('hidden'));
            v.muted = !withSound;
            setSoundLabel();
            v.currentTime = 0;
            const started = v.play();
            if (started) started.catch(() => { v.muted = true; setSoundLabel(); v.play().catch(profile); });
            // Видео так и не пошло: не держим человека перед пустым экраном.
            setTimeout(() => { if (!done && v.currentTime === 0) profile(); }, 8000);
          }
          $('withSound').addEventListener('click', () => watch(true));
          $('noSound').addEventListener('click', () => watch(false));
          $('skipPoster').addEventListener('click', e => { e.preventDefault(); profile(); });
          $('skip').addEventListener('click', e => { e.preventDefault(); profile(); });
          sound.addEventListener('click', () => { v.muted = !v.muted; setSoundLabel(); });
          v.addEventListener('timeupdate', () => { if (v.duration) $('p').style.width = (v.currentTime / v.duration * 100) + '%'; });
          v.addEventListener('ended', profile);
          v.addEventListener('error', () => { if (!$('poster').classList.contains('hidden')) return; profile(); });
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
