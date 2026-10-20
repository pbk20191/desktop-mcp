using Tomlyn.Model;
using Tomlyn.Serialization;

namespace DesktopMcp.Core.Config;

/// <summary>
/// Source-generated Tomlyn metadata. The file is read as an untyped table and checked by <see cref="ConfigFile"/>, so
/// the strict type rules and their messages are upstream's.
/// </summary>
[TomlSerializable(typeof(TomlTable))]
internal partial class ConfigTomlContext : TomlSerializerContext;
