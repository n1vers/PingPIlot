# PingPIlot

PingPIlot — Windows-приложение для мониторинга доступности компьютеров и сетевых устройств. Оно показывает задержку, потери пакетов и историю проверок, умеет группировать устройства, сканировать локальную сеть и сохранять сессии наблюдения.

## Возможности

- дашборд с плитками устройств, статусом, задержкой и потерями пакетов;
- график и матрица истории ping;
- группы компьютеров и быстрые действия для проверки;
- сканер локальной сети и диапазона IP;
- сохранённые сессии мониторинга и экспорт данных;
- тёмная и светлая темы, масштабирование интерфейса;
- сворачивание в трей и запуск вместе с Windows.

## Скачать

Готовая версия для Windows публикуется на странице [Releases](https://github.com/n1vers/PingPIlot/releases). Скачайте `PingPIlot-win-x64.exe` и запустите файл — установка не требуется.

## Запуск из исходников

Требуется Windows и .NET SDK 10.0:

```powershell
dotnet run --project .\PingPIlot\PingPIlot.csproj
```

Для локальной публикации одного `.exe`:

```powershell
dotnet publish .\PingPIlot\PingPIlot.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  --output .\publish
```

## Создание релиза

Для автоматической сборки и публикации `.exe` создайте и отправьте тег:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

GitHub Actions соберёт приложение на `windows-latest`, проверит проект и добавит `PingPIlot-win-x64.exe` в новый Release.
