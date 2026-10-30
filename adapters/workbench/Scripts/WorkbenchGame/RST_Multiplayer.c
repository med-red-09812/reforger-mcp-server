#ifdef WORKBENCH
//! Keep the stock launcher while supplying the mounted project's actual search roots.
class RST_MultiplayerPaths
{
	static bool TryGetArgument(out string argument)
	{
		string configured;
		System.GetCLIParam("addonsDir", configured);
		array<string> directories = {};
		if (!configured.IsEmpty())
		{
			if (configured.Contains("\""))
				return false;
			configured.Split(",", directories, true);
		}

		array<string> addons = {};
		GameProject.GetLoadedAddons(addons);
		foreach (string guid : addons)
		{
			if (GameProject.IsVanillaAddon(guid))
				continue;
			string path;
			if (!Workbench.GetAbsolutePath("$" + GameProject.GetAddonID(guid) + ":", path))
				return false;
			if (path.IsEmpty() || path.Contains(",") || path.Contains("\""))
				return false;
			if (directories.Find(path) == -1)
				directories.Insert(path);
		}

		argument = string.Empty;
		foreach (string directory : directories)
		{
			if (directory.IsEmpty() || directory.Contains("\""))
				return false;
			if (!argument.IsEmpty())
				argument += ",";
			argument += directory;
		}
		if (!argument.IsEmpty())
			argument = "\"" + argument + "\"";
		return true;
	}
}

[WorkbenchPluginAttribute(name: "Dedicated Server Tool", shortcut: "Ctrl+Shift+D", wbModules: { "WorldEditor" }, awesomeFontCode: 0xF233)]
modded class SCR_DedicatedServerPlugin
{
	protected override string GetAddonsDirCLI()
	{
		string argument;
		if (!RST_MultiplayerPaths.TryGetArgument(argument))
		{
			Print("Reforger MCP: unable to resolve unambiguous mounted addon search paths", LogLevel.ERROR);
			return string.Empty;
		}
		return argument;
	}
}
#endif
