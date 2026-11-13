# Сборка и запуск

## Что нужно

- .NET 10 SDK.
- RimWorld 1.7 (Steam).
- Мод Harmony из мастерской (id 2009463077), версия для 1.7 (Harmony 3).

## Переменные окружения

| Переменная | Значение | Пример |
|---|---|---|
| `RIMWORLD_DIR` | Папка игры, где лежит `Assembly-CSharp.dll` | `C:\Program Files (x86)\Steam\steamapps\common\RimWorld` |
| `HARMONY_PATH` | Полный путь к `0Harmony.dll` для 1.7 | найдите файл в `...\Steam\steamapps\workshop\content\294100\2009463077\` |

Проще всего: `powershell -ExecutionPolicy Bypass -File tools\setup.ps1` — скрипт сам найдёт игру и Harmony, задаст переменные, соберёт проект и подключит мод. Ниже то же самое вручную.

В папке мода Harmony лежат `0Harmony.dll` для разных версий игры. Для 1.7 это `...\2009463077\1.7\Assemblies\0Harmony.dll`.

PowerShell, один раз:

```powershell
setx RIMWORLD_DIR "C:\Program Files (x86)\Steam\steamapps\common\RimWorld"
setx HARMONY_PATH "<полный путь к 0Harmony.dll для 1.7>"
```

После `setx` откройте новое окно терминала.

## Сборка

```powershell
dotnet build RWMLWC.slnx -c Release
```

Сборки клиента появятся в `Mod/1.7/Assemblies/` (`RWMLWC.dll`, `RWMLWC.Shared.dll`).

Без файлов игры (например, в CI) собираются только эти проекты:

```powershell
dotnet build Source/Server/RWMLWC.Server.csproj
dotnet test Source/Tests/RWMLWC.Tests.csproj
```

## Подключение к игре

Сделайте связь на папку `Mod` в папке модов игры (права администратора не нужны):

```cmd
mklink /J "C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Mods\RWMLWC" "<путь к репозиторию>\Mod"
```

Если в папке игры нет каталога `Mods`, создайте его. Проверено на 1.7.5005: игра подхватывает локальные моды оттуда.

## Проверка (TST-001)

1. Включите Harmony и RWMLWC в списке модов, Harmony выше.
2. Перезапустите игру.
3. В логе должна быть строка `[RWMLWC] загружен, протокол v0` и не должно быть красных ошибок от мода.
