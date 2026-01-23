using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// Simple in-memory store for user context and memories
class MemoryStore
{
    private readonly Dictionary<string, string> memories = new();

    public void AddMemory(string key, string value)
    {
        memories[key] = value;
    }

    public string? GetMemory(string key)
    {
        return memories.TryGetValue(key, out var value) ? value : null;
    }

    public bool RemoveMemory(string key)
    {
        return memories.Remove(key);
    }

    public bool LoadFromFile(string filePath)
    {
        if (!File.Exists(filePath))
            return false;

        var json = File.ReadAllText(filePath);
        var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        if (data == null)
            return false;

        memories.Clear();
        foreach (var (key, value) in data)
        {
            memories[key] = value;
        }
        return true;
    }

    public void SaveToFile(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var json = System.Text.Json.JsonSerializer.Serialize(memories, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(filePath, json);
    }

    public string GetAllMemoriesAsContext()
    {
        if (memories.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("### User Context & Memories:");
        foreach (var (key, value) in memories)
        {
            sb.AppendLine($"- {key}: {value}");
        }
        return sb.ToString();
    }

    public void LoadDefaultUserProfile()
    {
        // Personal information for Pablo Piovano
        AddMemory("user_name", "Pablo Piovano");
        AddMemory("nickname", "Pablito Piova");
        AddMemory("title", "Microsoft MVP");
        AddMemory("interests", "Coffee, Argentine BBQ cooking, Traveling");
        AddMemory("location", "Sunchales, Santa Fe");
        AddMemory("country", "Argentina");
        AddMemory("friends", "Friend of Bruno and Quique");
    }
}
