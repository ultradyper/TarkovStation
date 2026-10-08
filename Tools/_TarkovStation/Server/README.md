# Запуск TarkovStation

Готовый Linux x64 пакет содержит сервер, ресурсы и соответствующий клиент. Требуется .NET Runtime 10. Для сборки из исходников нужен SDK из `global.json`.

## Из исходников

Из корня репозитория:

```bash
dotnet build Content.IntegrationTests/Content.IntegrationTests.csproj -c Release
dotnet run --project Content.Packaging -c Release --no-build -- client --skip-build --no-wipe-release --configuration Release
cp release/SS14.Client.zip bin/Content.Server/Content.Client.zip
Scripts/sh/runTarkovServer.sh
```

Скрипт использует `Resources/ConfigPresets/_TarkovStation/production.toml` и хранит данные в `userdata/tarkovstation`. Пути можно переопределить через `TARKOV_CONFIG_FILE` и `TARKOV_DATA_DIR`.

Перед публикацией клиентского или общего кода:

```bash
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj -c Release --no-build \
  --filter FullyQualifiedName=Content.IntegrationTests.Tests.Utility.SandboxTest.Test
```

Движок включён в публичное дерево исходников. Не обновляй его зависимости отдельно от согласованной версии сборки.

## Установка готового пакета на Ubuntu

Установи .NET Runtime 10 и распакуй сервер в `/opt/tarkovstation`. Создай отдельного пользователя и каталоги:

```bash
sudo useradd --system --user-group --home-dir /var/lib/tarkovstation --shell /usr/sbin/nologin tarkovstation
sudo install -d -m 750 -o tarkovstation -g tarkovstation /var/lib/tarkovstation
sudo install -d -m 755 /etc/tarkovstation
sudo cp /opt/tarkovstation/Resources/ConfigPresets/_TarkovStation/production.toml /etc/tarkovstation/server.toml
sudoedit /etc/tarkovstation/server.toml
```

В `[status]` укажи `connectaddress = "udp://PUBLIC_IP:1212"`. Если меняешь порт, одновременно измени `[net] port`, адрес подключения и правила firewall. Нужны TCP и UDP на выбранном порту.

Для публикации в общем списке лаунчера заполни раздел `[hub]` в `/etc/tarkovstation/server.toml`:

```toml
[hub]
advertise = true
server_url = "ss14://PUBLIC_IP:1212/"
tags = "lang:ru,rp:low,region:as_n"
```

`lang:ru` задаёт язык, `rp:low` уровень LRP. `region:as_n` подходит текущей VM в Казахстане; при переносе выбери регион фактического размещения по [карте регионов SS14](https://docs.spacestation14.com/en/robust-toolbox/server-http-api.html#tags). Явный `server_url` позволяет обойти зависимость от сервиса определения внешнего IP при старте. Адрес должен оставаться актуальным после выключения VM: закрепи внешний IP у провайдера или используй домен с актуальной DNS-записью.

В конфигурации установлены 64 подключения и 64 отображаемых слота. Счётчик занятых мест показывает реальных подключённых игроков. Название и описание находятся в `[game]`, ссылки Discord и GitHub в `[infolinks]`.

Production-конфигурация включает авторизацию SS14, отключает локальный обход авторизации, автоматический эвакуационный шаттл и станционные события, не относящиеся к режиму. Тестовых персонажей и команд выдачи денег в сборке нет.

Установи unit из репозитория:

```bash
sudo cp Tools/_TarkovStation/Server/tarkovstation.service /etc/systemd/system/tarkovstation.service
sudo systemctl daemon-reload
sudo systemctl enable --now tarkovstation
sudo journalctl -u tarkovstation -n 50 --no-pager
```

В готовом архиве этот unit находится в `deploy/tarkovstation.service`. Код принадлежит оператору, игровой пользователь пишет только в каталог данных. Дополнительные переменные окружения можно хранить в `/etc/tarkovstation/server.env`, вне Git.

Unit запускается вместе с системой и перезапускает завершившийся процесс через 10 секунд без ограничения числа попыток. `network-online.target` задаёт порядок запуска, но не гарантирует доступность интернета. Не удаляй `AF_NETLINK` из `RestrictAddressFamilies`: он нужен .NET для чтения сетевых интерфейсов.

Проверь автозапуск командой `systemctl is-enabled tarkovstation`, состояние процесса через `systemctl status tarkovstation`. `/status` должен отдавать название, `soft_max_players: 64` и теги; `/info?can_skip_build=1` описание и ссылки. Ошибки регистрации ищи в журнале по `hub:`. Работающий сервер ещё не означает успешную регистрацию: проверь наличие его адреса в `https://hub.spacestation14.com/api/servers`. При HTTP 500 от хаба автоматические попытки продолжаются; эту ошибку нельзя считать успешной публикацией.

Для ручного запуска сначала останови сервис, затем запусти `run-server.sh` от того же пользователя с теми же переменными окружения. В серверной консоли `promotehost ИМЯ_АККАУНТА` назначает владельца подключённому игроку. Права не выдаются автоматически по адресу или имени.

## Данные и обновление

- `preferences.db`: серверные профили SS14 и административные данные.
- `tarkovstation.db`: персонажи режима, кошельки, схрон, сделки, контракты и цикл.
- `logs/` и `voice_logs/`: журналы сервера.

Не запускай два процесса с одним каталогом данных. Перед обновлением сделай согласованную резервную копию:

```bash
python3 Tools/_TarkovStation/Server/backup.py /var/lib/tarkovstation /path/to/private-backups
```

Скрипт учитывает SQLite WAL и проверяет целостность. Для восстановления останови сервис и используй базы из одной копии.

Сборку и замену файлов выполняй после остановки процесса. В checkout упаковщик запускается с `--skip-build --no-wipe-release`, чтобы не очищать готовые каталоги. После замены запусти сервис и проверь журнал, `/status` и выдачу клиента.

При переходе со старой альфы останови процесс и перенеси `tarkov-alpha.db` в `tarkovstation.db` вместе с его WAL/SHM, если они остались. `preferences.db` сохраняется. Миграция схемы уберёт старые тестовые аккаунты и связанные сделки, вернёт реальные резервы игрокам. Персонажи, кошельки и вещи обычных игроков сохраняются.

Незавершённый рейд при перезапуске считается потерянным: физический мир не восстанавливается. Планируй обновление после окончания рейда. При аварии возможен откат последних несохранённых изменений носимого снаряжения в хабе.

Если используется Discord-экспортер, укажи ему новый путь `GAME_DATABASE` после переименования базы. Это read-only интеграция, Discord-токен игровому серверу не нужен.
