# dmc-mcp

MCP-сервер для публикации постпроцессоров в [Digital Machine Center](https://dmc.encycam.com)
из Cursor или Claude Code — так же, как `sprutcam-extension-mcp` публикует расширения СПРУТКАМа.

| Инструмент | Что делает |
|---|---|
| `publish_post` | Загружает `.sppx` / `.dll` / `.stnci` / `.zip` черновиком. Бэкенд разбирает архив, ИИ дописывает описание, стойку, станок и обложку. Возвращает ссылку на карточку. |
| `update_post` | Поправить поля, которые ИИ угадал неверно: имя, описание, стойка, станок, тип, оси. |
| `submit_post` | Отправить черновик на модерацию. Если чего-то не хватает — скажет, чего. |
| `check_post_status` | Черновик / на модерации / опубликован, ссылка. |

Публикация **не** отправляет на модерацию сама: сначала автор смотрит, что дописал ИИ.

## Установка (Cursor, Claude Code)

Нужен .NET 8 SDK.

```bash
dotnet tool install -g SprutTechnology.DmcMcp --add-source <папка с .nupkg>
dmc-mcp setup
```

`setup` прописывает сервер `dmc` в `~/.cursor/mcp.json` (сливая с чужими серверами), регистрирует
его в Claude Code, если тот установлен, и выполняет вход. После этого редактор нужно перезапустить.
`--no-login` пропускает вход.

Вход — `dmc-mcp login` — открывает страницу входа в браузере (учётная запись licsys); инструмент
хранит только refresh-токен в `%APPDATA%\dmc-mcp\auth.json` и пароля не видит. `--password` —
запасной путь без браузера. Нужна роль паблишера в DMC.

Руками вместо `setup` — `login` плюс в `~/.cursor/mcp.json`:

```json
{ "mcpServers": { "dmc": { "command": "dmc-mcp" } } }
```

## Как это выглядит

1. *«опубликуй пост C:\posts\fanuc-0i.sppx, это Fanuc 0i-MF для Haas VF-2»* → `publish_post` — ссылка
   на черновик и что заполнил ИИ.
2. *«стойка не Fanuc, а Siemens 828D»* → `update_post`.
3. *«отправь на модерацию»* → `submit_post`.
4. *«опубликовалось?»* → `check_post_status`.

## Настройки

| Переменная | Зачем |
|---|---|
| `DMC_API` | другой адрес API (стенд); по умолчанию — из `src/Brand.cs` |
| `DMC_SITE` | адрес сайта для ссылок на карточку |
| `DMC_TOKEN` | готовый токен вместо сохранённого входа (отладка, CI) |
| `DMC_CLIENT_ID`, `DMC_BROWSER_CLIENT_ID`, `DMC_KEYCLOAK_TOKEN_ENDPOINT` | другой клиент или Keycloak |

Все значения, привязывающие инструмент к DMC, лежат в одном файле — `src/Brand.cs`.

## Разработка

```bash
dotnet test tests/DmcMcp.Tests.csproj   # логика; DMC подделан, сети нет
dotnet run --project src                 # stdio-сервер, говорить с ним JSON-RPC
dotnet pack src -c Release -o pkg        # .nupkg инструмента
```
