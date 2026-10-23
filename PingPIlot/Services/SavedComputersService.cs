using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using PingPilot.Models;

namespace PingPilot.Services;

/// <summary>Хранит группы и компьютеры пользователя в AppData\Roaming\PingPilot.</summary>
public class SavedComputersService
{
    private readonly string _directoryPath;
    private readonly string _filePath;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public SavedComputersService()
    {
        _directoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PingPilot");

        _filePath = Path.Combine(_directoryPath, "computers.json");
    }

    public SavedComputersData Load()
    {
        if (!File.Exists(_filePath))
            return new SavedComputersData();

        string json = File.ReadAllText(_filePath);

        try
        {
            SavedComputersData data =
                JsonSerializer.Deserialize<SavedComputersData>(json, _jsonOptions)
                ?? new SavedComputersData();

            // Восстановление безопасных значений для старого или частично заполненного JSON.
            data.Groups ??= new List<ComputerGroup>();
            data.Computers ??= new List<SavedComputer>();
            data.ScanSettings ??= new ScannerSettings();
            if (data.ScanSettings.AutoScanIntervalSeconds < 10 || data.ScanSettings.AutoScanIntervalSeconds > 3600)
                data.ScanSettings.AutoScanIntervalSeconds = 60;
            if (data.ScanSettings.TargetMode is not "LocalNetwork" and not "IpRange")
                data.ScanSettings.TargetMode = "LocalNetwork";
            data.ScanSettings.RangeStart ??= string.Empty;
            data.ScanSettings.RangeEnd ??= string.Empty;
            data.Groups.RemoveAll(g => g == null || g.Id == Guid.Empty || string.IsNullOrWhiteSpace(g.Name));

            var groupIds = data.Groups.Select(g => g.Id).ToHashSet();
            data.Computers.RemoveAll(c =>
                c == null || c.Id == Guid.Empty || string.IsNullOrWhiteSpace(c.Host) ||
                !groupIds.Contains(c.GroupId));

            foreach (SavedComputer computer in data.Computers)
            {
                computer.Name = string.IsNullOrWhiteSpace(computer.Name)
                    ? computer.Host
                    : computer.Name.Trim();
                computer.Host = computer.Host.Trim();
                computer.Description ??= string.Empty;
            }

            return data;
        }
        catch (JsonException ex)
        {
            string backupPath = _filePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json";
            try
            {
                File.Copy(_filePath, backupPath, overwrite: false);
            }
            catch (Exception backupException)
            {
                throw new InvalidDataException(
                    $"The settings file is corrupted and a backup copy could not be created: {backupException.Message}", ex);
            }

            throw new InvalidDataException(
                $"The settings file is corrupted. A backup copy was saved to: {backupPath}", ex);
        }
    }

    public void Save(SavedComputersData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Directory.CreateDirectory(_directoryPath);

        string json = JsonSerializer.Serialize(data, _jsonOptions);
        string temporaryPath = _filePath + ".tmp";

        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _filePath, overwrite: true);
    }
}
