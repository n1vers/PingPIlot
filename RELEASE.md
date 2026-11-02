# Выпуск версии

## Подготовка

1. Обновите версию в GitHub Release или в сопроводительных материалах.
2. Проверьте `dotnet build .\PingPIlot\PingPIlot.csproj -c Release`.
3. Проверьте запуск на чистой Windows-машине.
4. Убедитесь, что в коммит не попали `bin/`, `obj/`, `artifacts/` и локальные данные.
5. Создайте и отправьте тег:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

## Автоматический релиз

Workflow `release.yml` запускается для тегов `v*`, собирает `win-x64` и создаёт ZIP в GitHub Release.

## Ручная публикация

```powershell
dotnet publish .\PingPIlot\PingPIlot.csproj -c Release -r win-x64 --self-contained false -o .\artifacts\PingPIlot
Compress-Archive -Path .\artifacts\PingPIlot\* -DestinationPath .\artifacts\PingPIlot-win-x64.zip -Force
```

Для автономного пакета используйте `--self-contained true`.
