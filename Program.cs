using Microsoft.Extensions.Configuration;

namespace GeoFileWatcher
{
    internal class Program
    {
        private static string _watchDirectory = string.Empty;
        private static readonly List<FileInfo> _existingFiles = new();
        private static readonly Dictionary<string, List<FileInfo>> _designationMap = new();
        private static FileSystemWatcher? _watcher;

        static void Main(string[] args)
        {
            // Загрузка настроек
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            _watchDirectory = configuration["WatchDirectory"] ?? throw new InvalidOperationException("WatchDirectory не указан в appsettings.json");

            if (!Directory.Exists(_watchDirectory))
            {
                Console.WriteLine($"Ошибка: Директория '{_watchDirectory}' не существует.");
                return;
            }

            Console.WriteLine($"Мониторинг директории: {_watchDirectory}");
            Console.WriteLine();

            // Шаг 1: Сканирование существующих файлов и проверка дубликатов
            ScanExistingFiles();

            // Шаг 2: Запуск FileWatcher
            StartFileWatcher();

            Console.WriteLine("Нажмите 'q' для выхода.");
            while (Console.ReadKey(true).Key != ConsoleKey.Q)
            {
                // Ожидание ввода пользователя
            }

            _watcher?.Dispose();
        }

        static void ScanExistingFiles()
        {
            Console.WriteLine("Сканирование существующих файлов...");

            var files = Directory.GetFiles(_watchDirectory, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => f.EndsWith(".geo", StringComparison.OrdinalIgnoreCase) || 
                            f.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
                .Select(f => new FileInfo(f))
                .ToList();

            foreach (var file in files)
            {
                ParseFileName(file.Name, out string designation, out string name);

                if (!_designationMap.ContainsKey(designation))
                {
                    _designationMap[designation] = new List<FileInfo>();
                }

                _designationMap[designation].Add(file);
                _existingFiles.Add(file);
            }

            // Проверка на дубликаты обозначений с разными наименованиями
            var duplicates = FindDuplicates();

            if (duplicates.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("!!! ОБНАРУЖЕНЫ ДУБЛИКАТЫ ОБОЗНАЧЕНИЙ С РАЗНЫМИ НАИМЕНОВАНИЯМИ !!!");
                Console.WriteLine();

                foreach (var dup in duplicates)
                {
                    Console.WriteLine($"Обозначение: {dup.Key}");
                    foreach (var file in dup.Value)
                    {
                        Console.WriteLine($"  - {file.Name}");
                    }
                    Console.WriteLine();
                }

                Console.WriteLine("Хотите удалить дубликаты? (y/n): ");
                var response = Console.ReadLine()?.Trim().ToLower();

                if (response == "y" || response == "д")
                {
                    DeleteDuplicates(duplicates);
                }
                else
                {
                    Console.WriteLine("Удаление дубликатов отменено пользователем.");
                }
            }
            else
            {
                Console.WriteLine("Дубликаты не найдены.");
            }

            Console.WriteLine();
        }

        static void ParseFileName(string fileName, out string designation, out string name)
        {
            // Формат: "обозначение"."наименование"."расширение"
            // Расширение уже известно (.geo или .dxf)
            var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
            
            // Находим последнюю точку для разделения обозначения и наименования
            // Обозначение может содержать точки (например, 12345.01.01)
            var lastDotIndex = nameWithoutExtension.LastIndexOf('.');

            if (lastDotIndex > 0)
            {
                designation = nameWithoutExtension.Substring(0, lastDotIndex);
                name = nameWithoutExtension.Substring(lastDotIndex + 1);
            }
            else
            {
                designation = nameWithoutExtension;
                name = string.Empty;
            }
        }

        static Dictionary<string, List<FileInfo>> FindDuplicates()
        {
            var duplicates = new Dictionary<string, List<FileInfo>>();

            foreach (var kvp in _designationMap)
            {
                if (kvp.Value.Count > 1)
                {
                    // Проверяем, есть ли разные наименования для одного обозначения
                    var names = kvp.Value.Select(f =>
                    {
                        ParseFileName(f.Name, out _, out string name);
                        return name.ToLowerInvariant();
                    }).Distinct().ToList();

                    if (names.Count > 1)
                    {
                        duplicates[kvp.Key] = kvp.Value;
                    }
                }
            }

            return duplicates;
        }

        static void DeleteDuplicates(Dictionary<string, List<FileInfo>> duplicates)
        {
            foreach (var kvp in duplicates)
            {
                Console.WriteLine($"\nОбозначение: {kvp.Key}");
                
                for (int i = 0; i < kvp.Value.Count; i++)
                {
                    Console.WriteLine($"{i + 1}. {kvp.Value[i].Name}");
                }

                Console.WriteLine("Введите номера файлов для удаления (через запятую), или оставьте пустым для пропуска: ");
                var input = Console.ReadLine()?.Trim();

                if (!string.IsNullOrEmpty(input))
                {
                    var indices = input.Split(',')
                        .Select(s => int.TryParse(s.Trim(), out var n) ? n : -1)
                        .Where(n => n > 0 && n <= kvp.Value.Count)
                        .Distinct()
                        .ToList();

                    foreach (var index in indices)
                    {
                        var fileToDelete = kvp.Value[index - 1];
                        try
                        {
                            File.Delete(fileToDelete.FullName);
                            _existingFiles.Remove(fileToDelete);
                            Console.WriteLine($"Удален файл: {fileToDelete.Name}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Ошибка при удалении {fileToDelete.Name}: {ex.Message}");
                        }
                    }
                }
            }

            // Обновляем карту обозначений после удаления
            _designationMap.Clear();
            foreach (var file in _existingFiles)
            {
                ParseFileName(file.Name, out string designation, out _);
                if (!_designationMap.ContainsKey(designation))
                {
                    _designationMap[designation] = new List<FileInfo>();
                }
                _designationMap[designation].Add(file);
            }
        }

        static void StartFileWatcher()
        {
            _watcher = new FileSystemWatcher(_watchDirectory)
            {
                IncludeSubdirectories = false,
                Filter = "*.*"
            };

            _watcher.Created += OnFileCreated;
            _watcher.Deleted += OnFileDeleted;
            _watcher.EnableRaisingEvents = true;

            Console.WriteLine("FileWatcher запущен. Ожидание новых файлов...");
            Console.WriteLine();
        }

        static void OnFileCreated(object sender, FileSystemEventArgs e)
        {
            // Игнорируем файлы не тех расширений
            if (!e.Name.EndsWith(".geo", StringComparison.OrdinalIgnoreCase) && 
                !e.Name.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Небольшая задержка, чтобы файл полностью записался
            System.Threading.Thread.Sleep(100);

            if (!File.Exists(e.FullPath))
            {
                return;
            }

            var newFile = new FileInfo(e.FullPath);
            ParseFileName(newFile.Name, out string designation, out string newName);

            // Проверяем, есть ли уже такое обозначение
            if (_designationMap.TryGetValue(designation, out var existingFiles))
            {
                // Проверяем, совпадает ли наименование
                var hasDifferentName = existingFiles.Any(f =>
                {
                    ParseFileName(f.Name, out _, out string existingName);
                    return !existingName.Equals(newName, StringComparison.OrdinalIgnoreCase);
                });

                if (hasDifferentName)
                {
                    Console.WriteLine();
                    Console.WriteLine("!!! ВНИМАНИЕ: Обнаружен конфликт обозначений !!!");
                    Console.WriteLine($"Новый файл: {newFile.Name}");
                    Console.WriteLine($"Обозначение '{designation}' уже используется другими файлами:");
                    foreach (var f in existingFiles)
                    {
                        Console.WriteLine($"  - {f.Name}");
                    }
                    Console.WriteLine();
                    Console.WriteLine("Требуется ручное вмешательство!");
                }
                else
                {
                    // То же обозначение и то же наименование - это нормальная ситуация (например, .geo и .dxf пара)
                    Console.WriteLine($"Файл добавлен: {newFile.Name} (дубль обозначения с тем же наименованием)");
                }
            }
            else
            {
                Console.WriteLine($"Новый файл добавлен: {newFile.Name}");
            }

            // Добавляем новый файл в списки
            if (!_designationMap.ContainsKey(designation))
            {
                _designationMap[designation] = new List<FileInfo>();
            }
            _designationMap[designation].Add(newFile);
            _existingFiles.Add(newFile);
        }

        static void OnFileDeleted(object sender, FileSystemEventArgs e)
        {
            // Игнорируем файлы не тех расширений
            if (!e.Name.EndsWith(".geo", StringComparison.OrdinalIgnoreCase) && 
                !e.Name.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var fileInfo = new FileInfo(e.FullPath);
            
            // Удаляем файл из списка существующих файлов
            var fileToRemove = _existingFiles.FirstOrDefault(f => f.FullName == e.FullPath);
            if (fileToRemove != null)
            {
                _existingFiles.Remove(fileToRemove);
                Console.WriteLine($"Файл удален: {e.Name}");

                // Обновляем карту обозначений
                ParseFileName(e.Name, out string designation, out _);
                if (_designationMap.TryGetValue(designation, out var filesList))
                {
                    filesList.RemoveAll(f => f.FullName == e.FullPath);
                    
                    // Если список пуст, удаляем запись из карты
                    if (filesList.Count == 0)
                    {
                        _designationMap.Remove(designation);
                    }
                }
            }
        }
    }
}
