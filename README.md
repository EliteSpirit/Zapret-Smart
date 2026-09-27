# Zapret Smart

GUI для обхода DPI на Windows со встроенным конструктором и поиском стратегий. Движок — форк `nfq/` из [zapret](https://github.com/bol-van/zapret) (winws + WinDivert).

## Состав

| Путь | Что это |
|---|---|
| `engine/nfq` | Исходники движка (форк zapret, MIT). Источник и коммит — `engine/UPSTREAM.txt` |
| `engine/files/fake` | Комплектные фейковые пакеты для стратегий |
| `src/ZapretSmart.Core` | Формат стратегий, белый список опций движка, сборка командной строки, запуск движка, настройки |
| `src/ZapretSmart.App` | Интерфейс (Avalonia) |
| `strategies/presets` | Встроенные стратегии |
| `lists` | Встроенные списки доменов |

## Формат стратегии

```json
{
  "id": "general-multisplit",
  "name": "Общая: fake + multisplit",
  "intercept": { "tcp": "80,443", "udp": "443" },
  "profiles": [
    { "hostlist": "general", "args": ["filter-tcp=443", "dpi-desync=fake,multisplit", "dpi-desync-split-pos=1,midsld"] }
  ]
}
```

`args` — опции движка без `--`, по порядку применения. Принимаются только опции из белого списка (`EngineOptionCatalog`) с проверенными значениями. Фейковые пакеты задаются только hex-строкой или именем комплектного файла. Пути к файлам, `--debug`, `--wf-raw`, hostlist/ipset в стратегии запрещены: стратегии могут приходить из сети, а движок работает с правами администратора.

## Сборка

Нужен .NET 8 SDK.

```
dotnet test
dotnet run --project src/ZapretSmart.App
```

Движок собирается в Cygwin (`gcc-core`, `make`, `zlib-devel`): `make -C engine/nfq cygwin64`. Готовую сборку (GUI + winws.exe + WinDivert + cygwin1.dll) собирает CI: артефакт `ZapretSmart-win-x64`.

Если задать `ZS_ENGINE=<путь к winws.exe или nfqws>`, тесты дополнительно прогонят все пресеты и все допустимые значения опций через `--dry-run` настоящего движка.

## Лицензия

MIT. Код движка — MIT, © bol-van (`engine/LICENSE.zapret.txt`). WinDivert — LGPL-3.0, поставляется отдельной DLL.
