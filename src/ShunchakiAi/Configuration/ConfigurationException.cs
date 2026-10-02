namespace ShunchakiAi.Configuration;

/// <summary>Raised for invalid user configuration (missing API key, bad flag, ...).</summary>
public sealed class ConfigurationException(string message) : Exception(message);
