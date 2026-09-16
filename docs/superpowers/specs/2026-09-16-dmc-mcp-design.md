# dmc-mcp — MCP-сервер публикации в Digital Machine Center

Дата: 2026-09-16. Одобрено в обсуждении; текст ждёт ревью.

## Зачем

Авторы постпроцессоров хотят публиковать в DMC агентом из редактора (Cursor, Claude Code) —
так же, как расширения СПРУТКАМа через `sprutcam-extension-mcp`. Первая версия — только
постпроцессоры; всё остальное вне объёма.

## Что это

Копия `sprutcam-extension-mcp` с заменой значений: .NET 8, `ModelContextProtocol` 1.4.1, stdio,
ставится как `dotnet tool`. Команда `dmc-mcp`, пакет `SprutTechnology.DmcMcp`, имя сервера
в настройках редактора — `dmc`.

Из образца переносится: `Program.cs` (подкоманды `login`/`setup`, запуск сервера, логи в stderr),
`BrowserLogin`, `StoreTokenProvider` → `DmcTokenProvider`, `SetupCommand`, `ProcessRunner`
(нужен только `setup`, для `claude mcp add`), `FakeProcessRunner` и тесты логина и `setup`.

Не переносится: шаблон расширения, `guides/`, `GuideTools`, `SdkPin`, `NextVersion`,
`PackageInfo`, `TemplateRenamer` — у DMC нет ни шаблона, ни SDK, ни локальной сборки.

### Brand.cs — единственный файл со значениями

| Значение | Чем заполняется |
|---|---|
| `Product` / `Cli` / `McpServerName` | `Digital Machine Center` / `dmc-mcp` / `dmc` |
| `Api` | `https://dmc.encycam.com/api` |
| `Site` | `https://dmc.encycam.com` — для ссылок на карточку `/product/<slug>` |
| `KeycloakUrl` / `KeycloakRealm` / `KeycloakClient` | `https://webservices.encycam.com/keycloak/` / `licsys` / `dealer-space` |

Проверено 2026-09-16 живым запросом: Keycloak encycam принимает loopback-redirect у `dealer-space`
(и у `digital-twins`); бэкенд DMC проверяет подпись и issuer, `aud` не проверяет — токен
`dealer-space` он принимает (так же работает локальная разработка веб-клиента).

Переменные окружения: `DMC_API` — другой адрес API (стенд); `DMC_TOKEN` — готовый токен вместо
сохранённого входа (отладка, CI).

## Инструменты

Все четыре возвращают текст для человека, как в образце. Ошибка — тоже текст: что случилось и что
делать; исключения наружу не выходят. Общие ответы: 401 → «выполни `dmc-mcp login`»; 403 → «нужна
роль паблишера в DMC — попроси её у администратора».

### `publish_post`

Параметры:
- `file` — путь к `.sppx`, `.dll`, `.stnci` или `.zip` с постом. Обязателен.
- `name` — подсказка имени (→ `X-AI-Name-Hint`).
- `description_hint` — подсказка для описания (→ `X-AI-Description-Hint`).
- `ai` — `true` по умолчанию: бэкенд дописывает описание, обложку и метаданные
  (`X-AI-Description`, `X-AI-Image`, `X-AI-Metadata`); `false` — только разбор архива.

Делает:
1. `POST /products/bulk-zip-async`, multipart `file`; заголовки `X-Import-Id` (новый GUID) и
   `X-AI-*` выше, подсказки URL-encoded. Ответ — `{ importId }`.
2. `GET /products/bulk-zip/{importId}/progress` каждые 2 с, пока `status` не станет `done`,
   `cancelled` или `error`. `queued` — очередь за чужим импортом, не зависание. Ждёт до 10 минут.
3. Для каждого элемента `result.components` (обычно один) по `productId` → `GET /products/{id}`: slug, имя, стойка, станок, оси,
   описание, обложка.

Создаёт **черновик** (`DRAFT`). На модерацию не отправляет — сначала автор смотрит, что дописал ИИ.

Возвращает: ссылку `{Site}/product/{slug}`, id, разобранные и дописанные поля (стойка, станок,
оси, описание — есть/нет; обложка — есть/нет), напоминание проверить и вызвать `submit_post`.

Ошибки: файл не найден или не того типа — сразу, без запроса; «An import is already running» —
сказать, что у автора уже идёт импорт, повторить позже; `result.errors[]` — как есть; по
истечении 10 минут — вернуть `importId` и сказать, что сервер продолжает, черновик появится в
кабинете в «My components».

### `update_post`

Параметры: `id` и любые из: `name`, `description`, `controller_manufacturer`,
`controller_series`, `controller_model`, `machine_manufacturer`, `machine_series`,
`machine_model`, `machine_type` (значение `MachineType`), `number_of_axes`.

Делает: `GET /products/{id}` → `PUT /products/{id}` со всеми полями текущей строки, поверх которых
только переданные. PUT у DMC полный, частичного нет. Файлы, цену, статусы, видимость не трогает.

Возвращает: какие поля изменились, было → стало.

### `submit_post`

Параметры: `id`.

Делает: `PATCH /products/{id}/status?status=PENDING_REVIEW`.

Ошибки: 400 «Missing required fields: …» (бэкенд проверяет имя, производителя станка, тип станка,
архив) — вернуть список и подсказать `update_post`; уже на модерации или опубликован — сказать,
ничего не менять.

### `check_post_status`

Параметры: `id` или slug.

Делает: `GET /products/{idOrSlug}`.

Возвращает: статус словами (черновик / на модерации / опубликован / отключён), ссылку, имя,
стойку, станок.

## Логин

`dmc-mcp login` — страница входа в браузере (authorization code + PKCE, loopback), хранится
только refresh-токен (`scope offline_access`) в `%APPDATA%\dmc-mcp\auth.json` — свой файл, не
общий с магазином расширений. `--password` — запасной путь без браузера. `dmc-mcp setup
[--no-login]` — прописывает сервер в `~/.cursor/mcp.json` (слиянием) и в Claude Code через его
CLI, затем логин. Всё как в образце.

## Тесты

xUnit, как в образце, без сети. Клиент DMC — интерфейс `IDmcClient` (upload, progress, get, put,
patch) с подделкой в тестах. Сценарии: успешная публикация; ошибка компонента в `errors[]`;
401 и 403; «import already running»; таймаут ожидания; `submit_post` с недостающими полями;
`update_post` переносит нетронутые поля без изменений. Тесты `BrowserLogin` и `SetupCommand` —
перенос из образца.

Живая проверка — один пост на проде, руками автора; инструмент сам на прод ничего не публикует
в ходе разработки.

## Вне объёма

Схемы и киты (bulk-zip их примет, но не обещаем и не проверяем); удаление поста; обновление
архива у существующего поста; HTTP-транспорт для хостовых агентов; публикация из CI; создание
репозитория для поста.

## Риски

- Бэкенд допускает один импорт на паблишера одновременно — второй `publish_post` подряд ждёт.
- Клиент MCP может оборвать долгий вызов — поэтому предел 10 минут и `importId` в ответе.
- ИИ может ошибиться в стойке или станке — для этого `update_post`, а `submit_post` отдельно.
