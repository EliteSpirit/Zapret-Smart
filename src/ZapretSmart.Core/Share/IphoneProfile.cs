using System.Net;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace ZapretSmart.Core.Share;

/// <summary>
/// Сеть, для которой iPhone получает прокси: обычно домашний Wi-Fi, к которому подключён ПК. Passphrase null —
/// пароля в профиле не будет (iPhone спросит его сам или возьмёт сохранённый); IsOpen — сеть без пароля.
/// </summary>
public sealed record WifiNetwork(string Ssid, string? Passphrase, IPAddress ProxyAddress, bool IsOpen = false);

/// <summary>
/// Профиль конфигурации iOS (.mobileconfig) с одной сетью Wi-Fi и автонастройкой прокси.
/// Прокси не «ручной», а через файл proxy.pac на ПК: так iPhone не остаётся без интернета, когда ПК выключен
/// или приложение закрыто. Страховка двойная: ProxyPACFallbackAllowed разрешает идти напрямую, если сам файл
/// недоступен, а в файле указано «PROXY ...; DIRECT», и при недоступном прокси iOS переходит на прямое соединение
/// для каждого нового соединения. Интервала проверки в настройках iOS нет, его решает система.
/// Такие профили ставятся без MDM: Safari скачивает файл, пользователь подтверждает установку в Настройках. Профиль не подписан, iOS так и покажет.
/// Идентификаторы постоянные, поэтому повторная установка заменяет прежний профиль, а не добавляет второй.
/// </summary>
public static class IphoneProfile
{
    public const string Identifier = "com.zapretsmart.share";

    public static string Build(WifiNetwork wifi, int port)
    {
        string E(string s) => SecurityElement.Escape(s);
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>PayloadContent</key>
              <array>
                <dict>
                  <key>PayloadType</key><string>com.apple.wifi.managed</string>
                  <key>PayloadVersion</key><integer>1</integer>
                  <key>PayloadIdentifier</key><string>{Identifier}.wifi</string>
                  <key>PayloadUUID</key><string>{Uuid("wifi")}</string>
                  <key>PayloadDisplayName</key><string>{E(wifi.Ssid)}</string>
                  <key>SSID_STR</key><string>{E(wifi.Ssid)}</string>
                  <key>HIDDEN_NETWORK</key><false/>
                  <key>AutoJoin</key><true/>
                  <key>EncryptionType</key><string>{(wifi.IsOpen ? "None" : "Any")}</string>{(wifi.Passphrase is null ? "" : $"\n                  <key>Password</key><string>{E(wifi.Passphrase)}</string>")}
                  <key>ProxyType</key><string>Auto</string>
                  <key>ProxyPACURL</key><string>{PacUrl(wifi, port)}</string>
                  <key>ProxyPACFallbackAllowed</key><true/>
                </dict>
              </array>
              <key>PayloadDisplayName</key><string>Zapret Smart: раздача с ПК</string>
              <key>PayloadDescription</key><string>Для сети «{E(wifi.Ssid)}» направляет интернет через ПК {wifi.ProxyAddress}:{port}, на котором работает обход блокировок. Если ПК выключен или приложение закрыто, iPhone в этой сети ходит в интернет напрямую. Удалить: Настройки → Основные → VPN и управление устройством.</string>
              <key>PayloadIdentifier</key><string>{Identifier}</string>
              <key>PayloadType</key><string>Configuration</string>
              <key>PayloadUUID</key><string>{Uuid("profile")}</string>
              <key>PayloadVersion</key><integer>1</integer>
              <key>PayloadRemovalDisallowed</key><false/>
            </dict>
            </plist>

            """;
    }

    public static string PacUrl(WifiNetwork wifi, int port) => $"http://{wifi.ProxyAddress}:{port}/proxy.pac";

    /// <summary>Постоянный UUID из строки: один и тот же у каждой сборки профиля.</summary>
    private static string Uuid(string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Identifier + "/" + name));
        return new Guid(hash.AsSpan(0, 16)).ToString().ToUpperInvariant();
    }
}
