# Remote Mouse — Windows

Сервер для Windows 11. Телефон в локальной сети управляет курсором, клавиатурой, текстом, громкостью и медиа.

Приложение сидит в трее. Панель открывает подключение (QR и 6-значный код), показывает активный телефон и список доверенных устройств. Порт `12345`: UDP discovery, REST `/pair` и `/status`, WebSocket `/ws` (JSON). Протокол v1 — этот JSON. Если `/pair` присылает `protocol_version: 2`, мышь, ползунок громкости и абсолютный seek идут бинарным UDP на `12346`; остальное остаётся на WebSocket. Состояние — `%AppData%\RemoteMouse\state.json`.

Обзор всей системы, контракт и то, что уже сделано на телефоне и на ПК: [.docs/readme.md](.docs/readme.md).

## Состав

| Путь | Что это |
|------|---------|
| `src/RemoteMouse.App` | WPF, трей, REST/WS/UDP, SMTC, текст в фокусе |
| `src/RemoteMouse.Core` | Протокол, pairing, состояние, мышь и клавиатура |
| `tests/RemoteMouse.Core.Tests` | Тесты ядра |
| `pc-stub` | Старый Python-стенд, до WPF |
| `.docs` | Контракты и обзор |

## Запуск

```powershell
dotnet run --project src/RemoteMouse.App/RemoteMouse.App.csproj
```

Нужен .NET 8. Если порт `12345` занят (второй экземпляр или `pc-stub`), трей покажет предупреждение.
