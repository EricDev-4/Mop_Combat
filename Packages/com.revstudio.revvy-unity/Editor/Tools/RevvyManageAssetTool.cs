using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace RevStudio.Revvy.Editor
{
    /// <summary>
    /// Tier-1 tool #2 (contract §4): search/list/import/refresh/move/delete project assets.
    ///
    /// Single multi-action tool rather than six narrow ones, per the token-economy
    /// convention in contract §3.
    /// </summary>
    public static class RevvyManageAssetTool
    {
        private const int DefaultLimit = 100;
        private const int MaxLimit = 1000;

        [RevvyTool(
            "manage_asset",
            "Search, inspect, import, move, delete, and refresh Unity project assets through AssetDatabase. " +
            "Actions: search (AssetDatabase filter syntax, e.g. 't:Material shiny'), list (direct children of a " +
            "folder), info (type/GUID/dependencies of one asset), import (reimport a path), refresh (rescan the " +
            "project), move (rename/relocate, preserves GUID), delete (move to OS trash), create_folder. " +
            "All paths are project-relative and must start with 'Assets/' or 'Packages/'.",
            Title = "Manage assets",
            Destructive = true,
            ReadOnlyActions = new[] { "search", "list", "info" },
            TimeoutMs = 120000)]
        public static RevvyJson ManageAsset(
            [RevvyToolParam("Operation to perform.",
                Enum = new[] { "search", "list", "info", "import", "refresh", "move", "delete", "create_folder" })]
            string action,
            [RevvyToolParam("AssetDatabase search filter for 'search' (e.g. 't:Prefab Player', 'l:Enemy').")]
            string filter = null,
            [RevvyToolParam("Target asset or folder path, project-relative (e.g. 'Assets/Art/Hero.prefab').")]
            string path = null,
            [RevvyToolParam("Destination path for 'move' and new-folder name for 'create_folder'.")]
            string destination = null,
            [RevvyToolParam("Folders to restrict 'search' to. Defaults to the whole project.")]
            string[] search_in_folders = null,
            [RevvyToolParam("Maximum results returned by 'search' and 'list'.")]
            int limit = DefaultLimit,
            [RevvyToolParam("For 'list': include assets in nested folders as well as direct children.")]
            bool recursive = false)
        {
            string normalized = (action ?? string.Empty).Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "search":
                    return Search(filter, search_in_folders, limit);
                case "list":
                    return List(path, limit, recursive);
                case "info":
                    return Info(path);
                case "import":
                    return Import(path);
                case "refresh":
                    return Refresh();
                case "move":
                    return Move(path, destination);
                case "delete":
                    return Delete(path);
                case "create_folder":
                    return CreateFolder(path, destination);
                default:
                    throw new RevvyToolException(
                        "Unknown action '" + action + "'. Expected one of: search, list, info, import, " +
                        "refresh, move, delete, create_folder.");
            }
        }

        // ------------------------------------------------------------------ actions

        private static RevvyJson Search(string filter, string[] folders, int limit)
        {
            string effectiveFilter = filter ?? string.Empty;
            string[] roots = null;
            if (folders != null && folders.Length > 0)
            {
                roots = new string[folders.Length];
                for (int i = 0; i < folders.Length; i++)
                {
                    roots[i] = RequireProjectPath(folders[i], "search_in_folders");
                }
            }

            string[] guids = roots != null
                ? AssetDatabase.FindAssets(effectiveFilter, roots)
                : AssetDatabase.FindAssets(effectiveFilter);

            int capped = ClampLimit(limit);
            RevvyJson assets = RevvyJson.Array();
            for (int i = 0; i < guids.Length && assets.Count < capped; i++)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(assetPath))
                {
                    continue;
                }

                assets.Add(DescribeAsset(assetPath, guids[i]));
            }

            return RevvyJson.Object()
                .Set("success", true)
                .Set("action", "search")
                .Set("filter", effectiveFilter)
                .Set("total_matches", guids.Length)
                .Set("returned", assets.Count)
                .Set("truncated", guids.Length > assets.Count)
                .Set("assets", assets);
        }

        private static RevvyJson List(string path, int limit, bool recursive)
        {
            string folder = RequireProjectPath(path, "path");
            if (!AssetDatabase.IsValidFolder(folder))
            {
                throw new RevvyToolException("Not a folder: " + folder);
            }

            int capped = ClampLimit(limit);
            string[] subFolders = AssetDatabase.GetSubFolders(folder);
            string[] guids = AssetDatabase.FindAssets(string.Empty, new[] { folder });

            RevvyJson assets = RevvyJson.Array();
            int matched = 0;
            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(assetPath) || AssetDatabase.IsValidFolder(assetPath))
                {
                    continue;
                }

                // FindAssets is always recursive; filter to direct children unless asked.
                if (!recursive && !IsDirectChild(folder, assetPath))
                {
                    continue;
                }

                matched++;
                if (assets.Count < capped)
                {
                    assets.Add(DescribeAsset(assetPath, guid));
                }
            }

            return RevvyJson.Object()
                .Set("success", true)
                .Set("action", "list")
                .Set("path", folder)
                .Set("recursive", recursive)
                .Set("sub_folders", RevvyJson.ArrayOf(subFolders))
                .Set("total_matches", matched)
                .Set("returned", assets.Count)
                .Set("truncated", matched > assets.Count)
                .Set("assets", assets);
        }

        private static RevvyJson Info(string path)
        {
            string assetPath = RequireProjectPath(path, "path");
            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid))
            {
                throw new RevvyToolException("No asset at " + assetPath);
            }

            RevvyJson info = DescribeAsset(assetPath, guid);
            info.Set("dependencies", RevvyJson.ArrayOf(AssetDatabase.GetDependencies(assetPath, false)));

            UnityEngine.Object main = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (main != null)
            {
                info.Set("main_asset_name", main.name ?? string.Empty);
            }

            AssetImporter importer = AssetImporter.GetAtPath(assetPath);
            if (importer != null)
            {
                info.Set("importer_type", importer.GetType().Name);
            }

            return RevvyJson.Object()
                .Set("success", true)
                .Set("action", "info")
                .Set("asset", info);
        }

        private static RevvyJson Import(string path)
        {
            string assetPath = RequireProjectPath(path, "path");
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            return RevvyJson.Object()
                .Set("success", true)
                .Set("action", "import")
                .Set("path", assetPath)
                .Set("exists", !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath)));
        }

        private static RevvyJson Refresh()
        {
            AssetDatabase.Refresh(ImportAssetOptions.Default);
            return RevvyJson.Object()
                .Set("success", true)
                .Set("action", "refresh")
                .Set("is_updating", EditorApplication.isUpdating);
        }

        private static RevvyJson Move(string path, string destination)
        {
            string from = RequireProjectPath(path, "path");
            string to = RequireProjectPath(destination, "destination");

            EnsureParentFolder(to);
            string error = AssetDatabase.MoveAsset(from, to);
            if (!string.IsNullOrEmpty(error))
            {
                throw new RevvyToolException("MoveAsset failed: " + error);
            }

            return RevvyJson.Object()
                .Set("success", true)
                .Set("action", "move")
                .Set("from", from)
                .Set("to", to)
                .Set("guid", AssetDatabase.AssetPathToGUID(to));
        }

        private static RevvyJson Delete(string path)
        {
            string assetPath = RequireProjectPath(path, "path");
            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath)))
            {
                throw new RevvyToolException("No asset at " + assetPath);
            }

            // MoveAssetToTrash over DeleteAsset: recoverable, matching the "destructive
            // but not unrecoverable" posture the UE side takes for asset deletion.
            if (!AssetDatabase.MoveAssetToTrash(assetPath))
            {
                throw new RevvyToolException("Failed to delete " + assetPath +
                                             " (asset may be locked or open in the editor).");
            }

            return RevvyJson.Object()
                .Set("success", true)
                .Set("action", "delete")
                .Set("path", assetPath)
                .Set("recoverable", true);
        }

        private static RevvyJson CreateFolder(string path, string destination)
        {
            string parent = RequireProjectPath(path, "path");
            if (string.IsNullOrEmpty(destination))
            {
                throw new RevvyToolException("create_folder requires 'destination' (the new folder name).");
            }

            if (destination.IndexOfAny(new[] { '/', '\\' }) >= 0)
            {
                throw new RevvyToolException("create_folder 'destination' must be a single folder name.");
            }

            string guid = AssetDatabase.CreateFolder(parent, destination);
            if (string.IsNullOrEmpty(guid))
            {
                throw new RevvyToolException("CreateFolder failed under " + parent);
            }

            return RevvyJson.Object()
                .Set("success", true)
                .Set("action", "create_folder")
                .Set("path", AssetDatabase.GUIDToAssetPath(guid))
                .Set("guid", guid);
        }

        // ------------------------------------------------------------------ helpers

        private static RevvyJson DescribeAsset(string assetPath, string guid)
        {
            Type type = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
            return RevvyJson.Object()
                .Set("path", assetPath)
                .Set("guid", guid ?? AssetDatabase.AssetPathToGUID(assetPath))
                .Set("name", Path.GetFileNameWithoutExtension(assetPath))
                .Set("type", type != null ? type.Name : "Unknown")
                .Set("is_folder", AssetDatabase.IsValidFolder(assetPath));
        }

        private static bool IsDirectChild(string folder, string assetPath)
        {
            string trimmed = folder.TrimEnd('/');
            if (!assetPath.StartsWith(trimmed + "/", StringComparison.Ordinal))
            {
                return false;
            }

            return assetPath.IndexOf('/', trimmed.Length + 1) < 0;
        }

        private static void EnsureParentFolder(string assetPath)
        {
            int slash = assetPath.LastIndexOf('/');
            if (slash <= 0)
            {
                return;
            }

            string parent = assetPath.Substring(0, slash);
            if (AssetDatabase.IsValidFolder(parent))
            {
                return;
            }

            throw new RevvyToolException(
                "Destination folder does not exist: " + parent + " (create it first with action=create_folder).");
        }

        private static int ClampLimit(int limit)
        {
            if (limit <= 0)
            {
                return DefaultLimit;
            }

            return Math.Min(limit, MaxLimit);
        }

        /// <summary>
        /// Validates a caller-supplied asset path. Rejects absolute paths, backslashes,
        /// and any traversal so a tool call cannot escape the project sandbox.
        /// </summary>
        internal static string RequireProjectPath(string value, string argumentName)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new RevvyToolException("Argument '" + argumentName + "' is required for this action.");
            }

            string normalized = value.Replace('\\', '/').Trim();
            while (normalized.EndsWith("/", StringComparison.Ordinal) && normalized.Length > 1)
            {
                normalized = normalized.Substring(0, normalized.Length - 1);
            }

            if (normalized.Contains("..") || normalized.StartsWith("/", StringComparison.Ordinal) ||
                (normalized.Length > 1 && normalized[1] == ':'))
            {
                throw new RevvyToolException(
                    "Path '" + value + "' must be project-relative; absolute paths and '..' are refused.");
            }

            // Exact roots or a real path under them — "AssetsElsewhere/x" must not pass.
            if (!string.Equals(normalized, "Assets", StringComparison.Ordinal) &&
                !normalized.StartsWith("Assets/", StringComparison.Ordinal) &&
                !string.Equals(normalized, "Packages", StringComparison.Ordinal) &&
                !normalized.StartsWith("Packages/", StringComparison.Ordinal))
            {
                throw new RevvyToolException(
                    "Path '" + value + "' must start with 'Assets/' or 'Packages/'.");
            }

            return normalized;
        }
    }
}
