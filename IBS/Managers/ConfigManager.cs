using System;
using System.Collections.Generic;
using System.IO;

using IBS.Common;

using Spectre.Console;

namespace IBS.Managers;

public static class ConfigManager
{
    private static readonly Dictionary<String, String> raw_config = [];
    //TODO include/exclude paths in-world?
    // - In case some items need to be kept for easier debug
    // - Could also use autoSpawnItemsm but that means publishing those separately too

    static ConfigManager()
    {
        if (!File.Exists(Constants.ConfigPath))
            return;
        AnsiConsole.MarkupLineInterpolated($"[aqua]Loading session config from {Constants.ConfigPath}[/]");
        var lines = File.ReadAllLines(Constants.ConfigPath);
        foreach (var line in lines)
        {
            if (String.IsNullOrWhiteSpace(line))
                continue;
            var parts = line.Split('=');
            if (parts.Length != 2)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Invalid config line: {line}[/]");
                continue;
            }
            raw_config[parts[0].Trim()] = parts[1].Trim();
        }
    }

    private static T GetOrGenInternal<T>(String key, Converter<String, T> value_loader, Func<(T, String)> value_generator)
    {
        T value;
        if (raw_config.TryGetValue(key, out var value_str))
        {
            try
            {
                value = value_loader(value_str);
                AnsiConsole.MarkupLineInterpolated($"[aqua]Loaded config key {key} with value {value_str}[/]");
                return value;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Failed to load config key {key} with value {value_str}: [{ex.GetType()}] {ex.Message}[/]");
            }
        }
        (value, value_str) = value_generator();
        raw_config[key] = value_str;
        File.AppendAllLines(Constants.ConfigPath, [$"{key}={value_str}"]);
        return value;
    }

    public static T GetOrInput<T>(String key, String prompt, Converter<String, T> value_parser)
    {
        return GetOrGenInternal(key, value_parser, () =>
        {
            AnsiConsole.MarkupInterpolated($"[yellow]Enter {prompt}:[/] ");
            var input = Console.ReadLine() ?? throw new InvalidOperationException($"Input stream ended");
            return (value_parser(input), input);
        });
    }

    public static String GetOrInput(String key, String prompt) =>
        GetOrInput(key, prompt, s => s);

}
