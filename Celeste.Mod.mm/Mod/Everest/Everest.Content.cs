using Celeste.Mod.Helpers;
using Celeste.Mod.Meta;
using MAB.DotIgnore;
using Microsoft.Xna.Framework.Graphics;
using Monocle;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Celeste.Mod {

    /// <summary>
    /// Special meta type for assets.
    /// A ModAsset with a Type field that subclasses from this will not log path conflicts.
    /// </summary>
    public abstract class AssetTypeNonConflict { }

    // Asset types for which we want to log conflicts (we also log for Texture2D and ObjModel)
    public sealed class AssetTypeAssembly { private AssetTypeAssembly() { } }
    public sealed class AssetTypeBank { private AssetTypeBank() { } }
    public sealed class AssetTypeGUIDs { private AssetTypeGUIDs() { } }
    public sealed class AssetTypeMap { private AssetTypeMap() { } }
    public sealed class AssetTypeObjModelExport { private AssetTypeObjModelExport() { } }
    public sealed class AssetTypeTutorial { private AssetTypeTutorial() { } }

    // Asset types for which conflicts are not important
    public sealed class AssetTypeDecalRegistry : AssetTypeNonConflict { private AssetTypeDecalRegistry() { } }
    public sealed class AssetTypeDialog : AssetTypeNonConflict { private AssetTypeDialog() { } }
    public sealed class AssetTypeDialogExport : AssetTypeNonConflict { private AssetTypeDialogExport() { } }
    public sealed class AssetTypeFont : AssetTypeNonConflict { private AssetTypeFont() { } }
    public sealed class AssetTypeDirectory : AssetTypeNonConflict { private AssetTypeDirectory() { } }
    public sealed class AssetTypeMetadataYaml : AssetTypeNonConflict { private AssetTypeMetadataYaml() { } }
    public sealed class AssetTypeSpriteBank : AssetTypeNonConflict { private AssetTypeSpriteBank() { } }
    public sealed class AssetTypeEverestIgnore : AssetTypeNonConflict { private AssetTypeEverestIgnore() { } }

    // Generic asset types
    public sealed class AssetTypeLua { private AssetTypeLua() { } }
    public sealed class AssetTypeText { private AssetTypeText() { } }
    public sealed class AssetTypeXml { private AssetTypeXml() { } }
    public sealed class AssetTypeYaml { private AssetTypeYaml() { } }

    // Delegate types.
    public delegate string TypeGuesser(string file, out Type type, out string format);

    // Source types.
    public abstract class ModContent : IDisposable {
        public virtual string DefaultName { get; }
        private string _Name;
        public string Name {
            get => !string.IsNullOrEmpty(_Name) ? _Name : DefaultName;
            set => _Name = value;
        }

        public EverestModuleMetadata Mod;

        public IgnoreList Ignore;

        public readonly List<ModAsset> List = new List<ModAsset>();
        public readonly Dictionary<string, ModAsset> Map = new Dictionary<string, ModAsset>();

        protected abstract void Crawl();
        internal void _Crawl() => Crawl();

        protected virtual void Add(string path, ModAsset asset) {
            if (Everest.Content.TryAdd(path, asset)) {
                List.Add(asset);
                Map[asset.PathVirtual] = asset;
            }
        }

        protected virtual void Update(string path, ModAsset next) {
            if (next == null) {
                Update(Everest.Content.Get<AssetTypeDirectory>(path), null);
                return;
            }

            next.PathVirtual = path;
            Update((ModAsset) null, next);
        }

        protected virtual void Update(ModAsset prev, ModAsset next) {
            if (prev != null) {
                int index = List.IndexOf(prev);

                if (next == null) {
                    Map.Remove(prev.PathVirtual);
                    if (index != -1)
                        List.RemoveAt(index);

                    Everest.Content.Update(prev, null);
                    foreach (ModAsset child in prev.Children.ToArray())
                        if (child.Source == this)
                            Update(child, null);

                } else {
                    Map[prev.PathVirtual] = next;
                    if (index != -1)
                        List[index] = next;
                    else
                        List.Add(next);

                    next.PathVirtual = prev.PathVirtual;
                    next.Type = prev.Type;
                    next.Format = prev.Format;

                    Everest.Content.Update(prev, next);
                    foreach (ModAsset child in prev.Children.ToArray())
                        if (child.Source == this)
                            Update(child, null);
                    foreach (ModAsset child in next.Children.ToArray())
                        if (child.Source == this)
                            Update((ModAsset) null, child);
                }

            } else if (next != null) {
                Map[next.PathVirtual] = next;
                List.Add(next);
                Everest.Content.Update(null, next);
                foreach (ModAsset child in next.Children.ToArray())
                    if (child.Source == this)
                        Update((ModAsset) null, child);
            }
        }

        private bool disposed = false;

        ~ModContent() {
            if (disposed)
                return;
            disposed = true;

            Dispose(false);
        }

        public void Dispose() {
            if (disposed)
                return;
            disposed = true;

            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing) {
        }
    }

    public class FileSystemModContent : ModContent {
        public override string DefaultName => System.IO.Path.GetFileName(Path);

        /// <summary>
        /// The path to the mod directory.
        /// </summary>
        public readonly string Path;

        private readonly Dictionary<string, FileSystemModAsset> FileSystemMap = new Dictionary<string, FileSystemModAsset>();

        private FileSystemWatcher watcher;

        public FileSystemModContent(string path) {
            Path = path;

            try {
                watcher = new FileSystemWatcher {
                    Path = path,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                    IncludeSubdirectories = true
                };

                watcher.Changed += FileUpdated;
                watcher.Created += FileUpdated;
                watcher.Deleted += FileUpdated;
                watcher.Renamed += FileRenamed;

                watcher.EnableRaisingEvents = true;
            } catch (Exception e) {
                Logger.Warn("content", $"Failed watching folder: {path}");
                Logger.LogDetailed(e);
                watcher?.Dispose();
                watcher = null;
            }
        }

        protected override void Dispose(bool disposing) {
            base.Dispose(disposing);
            watcher?.Dispose();
        }

        protected override void Crawl() => Crawl(null, Path, false);

        protected virtual void Crawl(string dir, string root, bool update) {
            if (dir == null)
                dir = Path;
            if (root == null)
                root = Path;

            int lastIndexOfSlash = dir.LastIndexOf(System.IO.Path.DirectorySeparatorChar);
            // Ignore hidden files and directories.
            if (lastIndexOfSlash != -1 &&
                lastIndexOfSlash >= root.Length && // Make sure to not skip crawling in hidden mods.
                dir.Length > lastIndexOfSlash + 1 &&
                dir[lastIndexOfSlash + 1] == '.') {
                // Logger.Verbose("content", $"Skipped crawling hidden file or directory {dir.Substring(root.Length + 1)}");
                return;
            }

            if (File.Exists(dir)) {
                string path = dir.Substring(root.Length + 1);
                ModAsset asset = new FileSystemModAsset(this, dir);

                if (update)
                    Update(path, asset);
                else
                    Add(path, asset);
                return;
            }

            string[] files = Directory.GetFiles(dir);
            for (int i = 0; i < files.Length; i++) {
                string file = files[i];
                Crawl(file, root, update);
            }

            files = Directory.GetDirectories(dir);
            for (int i = 0; i < files.Length; i++) {
                string file = files[i];
                Crawl(file, root, update);
            }
        }

        protected override void Add(string path, ModAsset asset) {
            FileSystemModAsset fsma = (FileSystemModAsset) asset;
            FileSystemMap[fsma.Path] = fsma;
            base.Add(path, asset);
        }

        protected override void Update(string path, ModAsset next) {
            if (next is FileSystemModAsset fsma) {
                FileSystemMap[fsma.Path] = fsma;
            }
            base.Update(path, next);
        }

        protected override void Update(ModAsset prev, ModAsset next) {
            if (prev is FileSystemModAsset fsma) {
                FileSystemMap[fsma.Path] = null;
            }

            if ((fsma = next as FileSystemModAsset) != null) {
                FileSystemMap[fsma.Path] = fsma;

                // Make sure to wait until the file is readable.
                Stopwatch timer = Stopwatch.StartNew();
                while (File.Exists(fsma.Path)) {
                    try {
                        new FileStream(fsma.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete).Dispose();
                        break;
                    } catch (ThreadAbortException) {
                        throw;
                    } catch (ThreadInterruptedException) {
                        throw;
                    } catch {
                        // Retry, but not infinitely.
                        if (timer.Elapsed.TotalSeconds >= 2D)
                            throw;
                    }
                }
                timer.Stop();
            }

            base.Update(prev, next);
        }

        private void FileUpdated(object source, FileSystemEventArgs e) {
            // Directories will be "changed" as soon as their children change.
            if (e.ChangeType == WatcherChangeTypes.Changed && Directory.Exists(e.FullPath))
                return;

            Logger.Verbose("content", $"File updated: {e.FullPath} - {e.ChangeType}");
            QueuedTaskHelper.Do(e.FullPath, () => Update(e.FullPath, e.FullPath));
        }

        private void FileRenamed(object source, RenamedEventArgs e) {
            Logger.Verbose("content", $"File renamed: {e.OldFullPath} - {e.FullPath}");
            QueuedTaskHelper.Do(Tuple.Create(e.OldFullPath, e.FullPath), () => Update(e.OldFullPath, e.FullPath));
        }

        private void Update(string pathPrev, string pathNext) {
            ModAsset prev;
            if (FileSystemMap.TryGetValue(pathPrev, out FileSystemModAsset prevFS))
                prev = prevFS;
            else
                prev = Everest.Content.Get<AssetTypeDirectory>(pathPrev.Substring(Path.Length + 1));

            if (File.Exists(pathNext)) {
                if (prev != null)
                    Update(prev, new FileSystemModAsset(this, pathNext));
                else
                    Update(pathNext.Substring(Path.Length + 1), new FileSystemModAsset(this, pathNext));

            } else if (Directory.Exists(pathNext)) {
                Update(prev, null);
                Crawl(pathNext, Path, true);

            } else if (prev != null) {
                Update(prev, null);

            } else {
                Update(pathPrev, (ModAsset) null);
            }
        }
    }

    public class MapBinsInModsModContent : ModContent {
        public override string DefaultName => System.IO.Path.GetFileName(Path);

        /// <summary>
        /// The path to the mod directory.
        /// </summary>
        public readonly string Path;

        public MapBinsInModsModContent(string path) {
            Path = path;
        }

        protected override void Crawl() {
            string[] files = Directory.GetFiles(Path);
            for (int i = 0; i < files.Length; i++) {
                string file = files[i];
                string name = file.Substring(Path.Length + 1);
                if (!file.EndsWith(".bin") || !Everest.Loader.ShouldLoadFile(name))
                    continue;
                Add("Maps/" + file.Substring(Path.Length + 1), new MapBinsInModsModAsset(this, file));
            }
        }
    }

    public class AssemblyModContent : ModContent {
        public override string DefaultName => Assembly.GetName().Name;

        /// <summary>
        /// The assembly containing the mod content as resources.
        /// </summary>
        public readonly Assembly Assembly;

        public AssemblyModContent(Assembly asm) {
            Assembly = asm;
        }

        protected override void Crawl() {
            string[] resourceNames = Assembly.GetManifestResourceNames();
            for (int i = 0; i < resourceNames.Length; i++) {
                string name = resourceNames[i];
                int indexOfContent = name.IndexOf("Content");
                if (indexOfContent < 0)
                    continue;
                name = name.Substring(indexOfContent + 8);
                Add(name, new AssemblyModAsset(this, resourceNames[i]));
            }
        }
    }

    public class ZipModContent : ModContent {
        public override string DefaultName => System.IO.Path.GetFileName(Path);

        /// <summary>
        /// The path to the archive containing the mod content.
        /// </summary>
        public readonly string Path;

        public ZipModContent(string path) {
            Path = path;
        }

        private ZipArchive SharedZip;
        private int SharedZipUsers = 0;
        private readonly object SharedZipLock = new object();
        private bool disposed = false;

        private readonly object ReadingLock = new object();

        /// <summary>
        /// Object granting access to a shared ZipArchive instance, keeping track of the number of usages
        /// to dispose the shared instance once no one uses it anymore.
        /// </summary>
        public class ZipFileAccessor : IDisposable {
            private ZipModContent Parent;

            /// <summary>
            /// The loaded archive containing the mod content.
            /// </summary>
            public ZipArchive Zip => Parent.SharedZip;

            internal ZipFileAccessor(ZipModContent parent) {
                Parent = parent;
            }

            private bool disposed = false;

            public void Dispose() {
                if (disposed) return;
                disposed = true;

                lock (Parent.SharedZipLock) {
                    Parent.SharedZipUsers--;
                    if (Parent.SharedZipUsers > 0) return;

                    // if the file goes unused for 10 seconds, close it
                    QueuedTaskHelper.Do("SharedZipRelease-" + Parent.Path, delay: 10, DisposeParentZip);
                }
            }

            private void DisposeParentZip() {
                lock (Parent.SharedZipLock) {
                    // the task should get canceled when a new user shows up, but you never know...
                    if (Parent.SharedZipUsers > 0) return;

                    Logger.Debug("ZipModContent", $"Closing zip: {Parent.Path}");
                    Parent.SharedZip.Dispose();
                    Parent.SharedZip = null;
                }
            }
        }

        public ZipFileAccessor Open() {
            lock (SharedZipLock) {
                if (disposed) throw new ObjectDisposedException(nameof(ZipModContent));

                if (SharedZip == null) {
                    Logger.Debug("ZipModContent", $"Opening zip: {Path}");
                    SharedZip = ZipFile.OpenRead(Path);
                }

                SharedZipUsers++;
                QueuedTaskHelper.Cancel("SharedZipRelease-" + Path);
                return new ZipFileAccessor(this);
            }
        }

        protected override void Crawl() {
            using ZipFileAccessor zip = Open();

            foreach (ZipArchiveEntry entry in zip.Zip.Entries) {
                string entryName = entry.FullName.Replace('\\', '/');
                if (entryName.EndsWith("/")) continue;
                Add(entryName, new ZipModAsset(this, entry.FullName));
            }
        }

        public MemoryStream GetContents(string path) {
            lock (ReadingLock) {
                using ZipFileAccessor zip = Open();
                ZipArchiveEntry entry = zip.Zip.GetEntry(path);
                if (entry == null) throw new KeyNotFoundException($"File {path} not found in archive {Path}");
                return entry.ExtractStream();
            }
        }

        protected override void Dispose(bool disposing) {
            base.Dispose(disposing);

            if (disposed) return;

            lock (ReadingLock) {
                disposed = true;
                SharedZip.Dispose();
                SharedZip = null;
            }
        }
    }

    // Main helper type.
    public static partial class Everest {
        public static class Content {

            /// <summary>
            /// Whether or not Everest should dump all game assets into a user-friendly format on load (technically on Process).
            /// </summary>
            public static bool DumpOnLoad = false;
            internal static bool _DumpAll = false;

            /// <summary>
            /// The path to the original /Content directory.
            /// </summary>
            public static string PathContentOrig { get; internal set; }
            /// <summary>
            /// The path to the Everest /ModDUMP directory.
            /// </summary>
            public static string PathDUMP { get; internal set; }

            /// <summary>
            /// List of all currently loaded content mods.
            /// </summary>
            public readonly static List<ModContent> Mods = new List<ModContent>();

            /// <summary>
            /// Mod content mapping. Use Everest.Content.Add, Get, and TryGet where applicable instead.
            /// </summary>
            public readonly static Dictionary<string, ModAsset> Map = new Dictionary<string, ModAsset>();

            // Used to blacklists assets for ingest
            internal readonly static HashSet<string> BlacklistRootFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                "changelog", "credits", "documentation", "FAQ", "LICENSE", "README"
            };

            internal readonly static HashSet<string> BlacklistExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                ".cs", ".csproj", ".md", ".pdb", ".sln", ".yaml-backup", ".gitignore"
            };

            internal readonly static HashSet<string> BlacklistRootFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                "Ahorn", "Loenn"
            };

            internal readonly static HashSet<string> BlacklistFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                "lib-stripped", "__MACOSX", "obj"
            };

            internal readonly static List<string> LoadedAssetPaths = new List<string>();
            internal readonly static List<string> LoadedAssetFullPaths = new List<string>();
            internal readonly static List<WeakReference> LoadedAssets = new List<WeakReference>();

            internal readonly static char[] DirSplit = { '/' };

            internal static void Initialize() {
                Celeste.Instance.Content = new EverestContentManager(Celeste.Instance.Content);

                Directory.CreateDirectory(PathContentOrig = Path.Combine(PathGame, Celeste.Instance.Content.RootDirectory));
                PathDUMP = Path.Combine(PathEverest, "ModDUMP");

                Crawl(new AssemblyModContent(typeof(Everest).Assembly) {
                    Name = "Everest",
                    // Mod = CoreModule.Instance.Metadata // Can't actually set Mod this early.
                });
            }

            /// <summary>
            /// Gets the ModAsset mapped to the given relative path.
            /// </summary>
            /// <param name="path">The relative asset path.</param>
            /// <param name="metadata">The resulting mod asset meta object.</param>
            /// <param name="includeDirs">Whether to include directories or not.</param>
            /// <returns>True if a mapping for the given path is present, false otherwise.</returns>
            public static bool TryGet(string path, out ModAsset metadata, bool includeDirs = false) {
                path = path.Replace('\\', '/');

                lock (Map)
                    if (Map.TryGetValue(path, out metadata) && metadata != null)
                        return true;

                metadata = null;
                return false;
            }
            /// <summary>
            /// Gets the ModAsset mapped to the given relative path.
            /// </summary>
            /// <param name="path">The relative asset path.</param>
            /// <param name="includeDirs">Whether to include directories or not.</param>
            /// <returns>The resulting mod asset meta object, or null.</returns>
            public static ModAsset Get(string path, bool includeDirs = false) {
                if (TryGet(path, out ModAsset metadata, includeDirs))
                    return metadata;
                return null;
            }

            /// <summary>
            /// Gets the ModAsset mapped to the given relative path.
            /// </summary>
            /// <param name="path">The relative asset path.</param>
            /// <param name="metadata">The resulting mod asset meta object.</param>
            /// <param name="includeDirs">Whether to include directories or not.</param>
            /// <returns>True if a mapping for the given path is present, false otherwise.</returns>
            public static bool TryGet<T>(string path, out ModAsset metadata, bool includeDirs = false) {
                path = path.Replace('\\', '/');

                List<string> parts = new List<string>(path.Split(DirSplit, StringSplitOptions.RemoveEmptyEntries));
                for (int i = 0; i < parts.Count; i++) {
                    string part = parts[i];

                    if (part == "..") {
                        parts.RemoveAt(i);
                        parts.RemoveAt(i - 1);
                        i -= 2;
                        continue;
                    }

                    if (part == ".") {
                        parts.RemoveAt(i);
                        i -= 1;
                        continue;
                    }
                }

                path = string.Join("/", parts);

                lock (Map)
                    if (Map.TryGetValue(path, out metadata) && metadata != null && metadata.Type == typeof(T))
                        return true;

                metadata = null;
                return false;
            }
            /// <summary>
            /// Gets the ModAsset mapped to the given relative path.
            /// </summary>
            /// <param name="path">The relative asset path.</param>
            /// <param name="includeDirs">Whether to include directories or not.</param>
            /// <returns>The resulting mod asset meta object, or null.</returns>
            public static ModAsset Get<T>(string path, bool includeDirs = false) {
                if (TryGet<T>(path, out ModAsset metadata, includeDirs))
                    return metadata;
                return null;
            }

            public static bool TryAdd(string path, ModAsset metadata) {
                path = path.Replace('\\', '/');

                string filename = Path.GetFileNameWithoutExtension(path);
                if (filename.StartsWith(".") || BlacklistExtensions.Contains(Path.GetExtension(path)))
                    return false;

                string[] pathSplit = path.Split(DirSplit, StringSplitOptions.RemoveEmptyEntries);
                if (pathSplit.Length == 1 && BlacklistRootFiles.Contains(filename))
                    return false;

                for (int i = 0; i < pathSplit.Length - 1; i++) {
                    if (pathSplit[i].StartsWith(".") || BlacklistFolders.Contains(pathSplit[i]) || (i == 0 && BlacklistRootFolders.Contains(pathSplit[0])))
                        return false;
                }

                if (metadata != null &&
                    (metadata.Source?.Ignore?.IsIgnored(path, metadata.Type == typeof(AssetTypeDirectory)) ?? false)) {
                    return false;
                }

                if (metadata != null) {
                    if (metadata.Type == null)
                        path = GuessType(path, out metadata.Type, out metadata.Format);
                    metadata.PathVirtual = path;
                }
                string prefix = metadata?.Source?.Name;

                if (metadata != null && metadata.Type == typeof(AssetTypeDirectory) && !(metadata is ModAssetBranch))
                    return false;

                lock (Map) {
                    // We want our new mapping to replace the previous one, but need to replace the previous one in the shadow structure.
                    if (!Map.TryGetValue(path, out ModAsset metadataPrev))
                        metadataPrev = null;

                    if (metadata == null && metadataPrev != null && metadataPrev.Type == typeof(AssetTypeDirectory))
                        return false;

                    if (metadata == null) {
                        Map.Remove(path);
                        if (prefix != null)
                            Map.Remove($"{prefix}:/{path}");

                    } else {
                        if (Map.TryGetValue(path, out ModAsset existing) && existing != null && existing.Source != metadata.Source && !existing.Type.IsSubclassOf(typeof(AssetTypeNonConflict))) {
                            Logger.Warn("content", $"CONFLICT for asset path {path} ({existing?.Source?.Name ?? "???"} vs {metadata?.Source?.Name ?? "???"})");
                        }

                        Map[path] = metadata;
                        if (prefix != null)
                            Map[$"{prefix}:/{path}"] = metadata;
                    }

                    // If we're not already the highest level shadow "node"...
                    if (path != "") {
                        // Add directories automatically.
                        string pathDir = Path.GetDirectoryName(path).Replace('\\', '/');
                        if (!Map.TryGetValue(pathDir, out ModAsset metadataDir)) {
                            metadataDir = new ModAssetBranch {
                                PathVirtual = pathDir,
                                Type = typeof(AssetTypeDirectory)
                            };
                            Add(pathDir, metadataDir);
                        }
                        // If a previous mapping exists, replace it in the shadow structure.
                        lock (metadataDir.Children) {
                            int metadataPrevIndex = metadataDir.Children.IndexOf(metadataPrev);
                            if (metadataPrevIndex != -1) {
                                if (metadata == null) {
                                    metadataDir.Children.RemoveAt(metadataPrevIndex);
                                } else {
                                    metadataDir.Children[metadataPrevIndex] = metadata;
                                }
                            } else {
                                metadataDir.Children.Add(metadata);
                            }
                        }
                    }
                }

                return true;
            }

            /// <summary>
            /// Adds a new mapping for the given relative content path.
            /// </summary>
            /// <param name="path">The relative asset path.</param>
            /// <param name="metadata">The matching mod asset meta object.</param>
            public static void Add(string path, ModAsset metadata)
                => TryAdd(path, metadata);

            /// <summary>
            /// Invoked when GuessType can't guess the asset type.
            /// Subscribe to this event to register your own custom types.
            /// </summary>
            public static event TypeGuesser OnGuessType;
            /// <summary>
            /// Guess the file type and format based on its path.
            /// </summary>
            /// <param name="file">The relative asset path.</param>
            /// <param name="type">The file type.</param>
            /// <param name="format">The file format (file ending).</param>
            /// <returns>The passed asset path, trimmed if required.</returns>
            public static string GuessType(string file, out Type type, out string format) {
                type = typeof(object);
                format = Path.GetExtension(file) ?? "";

                if (format.Length < 1)
                    return file;

                format = format[1..];

                ReadOnlySpan<char> fileSpan = file.AsSpan();
                int fileSeparator = fileSpan.LastIndexOf('/') + 1;

                // folder with / at the end
                ReadOnlySpan<char> directorySpan = fileSpan[..fileSeparator]; // don't use Path.GetDirectoryName as it replaces '/' with '\' :catplush:
                // file
                ReadOnlySpan<char> fileNameSpan = fileSpan[fileSeparator..];
                // file without extension
                ReadOnlySpan<char> fileNameOnlySpan = Path.GetFileNameWithoutExtension(fileNameSpan);

                bool warningAlreadySent = false;

                if (MatchExtension(fileSpan, fileNameSpan, "dll", ref warningAlreadySent)) {
                    type = typeof(AssetTypeAssembly);
                    return fileSpan.ToString();
                }

                if (MatchExtension(fileSpan, fileNameSpan, "png", ref warningAlreadySent)) {
                    type = typeof(Texture2D);
                    return fileSpan[..^4].ToString();
                }

                if (MatchExtension(fileSpan, fileNameSpan, "obj", ref warningAlreadySent, isTextBased: true)) {
                    type = typeof(ObjModel);
                    return fileSpan[..^4].ToString();
                }

                if (MatchMultipartExtension(fileSpan, fileNameSpan, "obj.export", ref warningAlreadySent)) {
                    type = typeof(AssetTypeObjModelExport);
                    return fileSpan[..^7].ToString();
                }

                if (MatchExtension(fileSpan, fileNameSpan, "yaml", ref warningAlreadySent, isTextBased: true)
                    && directorySpan.IsEmpty
                    && SpanEqualsAny(fileNameOnlySpan, "metadata", "multimetadata", "everest")) {
                    type = typeof(AssetTypeMetadataYaml);
                    format = "yml";
                    return fileSpan[..^5].ToString();
                }

                if (MatchExtension(fileSpan, fileNameSpan, "yml", ref warningAlreadySent, isTextBased: true)
                    && directorySpan.IsEmpty
                    && SpanEquals(fileNameOnlySpan, "everest")) {
                    type = typeof(AssetTypeMetadataYaml);
                    return fileSpan[..^4].ToString();
                }

                if (MatchExtension(fileSpan, fileNameSpan, "xml", ref warningAlreadySent, isTextBased: true)) {
                    if (directorySpan.IsEmpty && SpanEquals(fileNameOnlySpan, "DecalRegistry")) {
                        type = typeof(AssetTypeDecalRegistry);
                        return fileSpan[..^4].ToString();
                    }
                    if (SpanEquals(directorySpan, "Graphics/") && SpanEqualsAny(fileNameOnlySpan, "Sprites", "SpritesGui", "Portraits")) {
                        type = typeof(AssetTypeSpriteBank);
                        return fileSpan[..^4].ToString();
                    }
                }

                if (directorySpan.IsEmpty && fileNameOnlySpan.IsEmpty && MatchExtension(fileSpan, fileNameSpan, "everestignore", ref warningAlreadySent, isTextBased: true)) {
                    type = typeof(AssetTypeEverestIgnore);
                    return "";
                }

                if (directorySpan.StartsWith("Dialog/")) {
                    if (MatchExtension(fileSpan, fileNameSpan, "txt", ref warningAlreadySent, isTextBased: true)) {
                        type = typeof(AssetTypeDialog);
                        return fileSpan[..^4].ToString();
                    }
                    if (MatchMultipartExtension(fileSpan, fileNameSpan, "txt.export", ref warningAlreadySent)) {
                        type = typeof(AssetTypeDialogExport);
                        return fileSpan[..^7].ToString();
                    }
                    if (MatchExtension(fileSpan, fileNameSpan, "fnt", ref warningAlreadySent, isTextBased: true)) {
                        type = typeof(AssetTypeFont);
                        return fileSpan[..^4].ToString();
                    }
                }

                if (MatchExtension(fileSpan, fileNameSpan, "bin", ref warningAlreadySent)) {
                    if (directorySpan.StartsWith("Maps/")) {
                        type = typeof(AssetTypeMap);
                        return fileSpan[..^4].ToString();
                    }
                    if (directorySpan.StartsWith("Tutorials/")) {
                        type = typeof(AssetTypeTutorial);
                        return fileSpan[..^4].ToString();
                    }
                }

                if (directorySpan.StartsWith("Audio/")) {
                    if (MatchExtension(fileSpan, fileNameSpan, "bank", ref warningAlreadySent)) {
                        type = typeof(AssetTypeBank);
                        return fileSpan[..^5].ToString();
                    }
                    if (MatchMultipartExtension(fileSpan, fileNameSpan, "guids.txt", ref warningAlreadySent, isTextBased: true)) {
                        type = typeof(AssetTypeGUIDs);
                        return fileSpan[..^4].ToString();
                    }
                    if (MatchMultipartExtension(fileSpan, fileNameSpan, "GUIDs.txt", ref warningAlreadySent, isTextBased: true)) {
                        // default fmod casing
                        type = typeof(AssetTypeGUIDs);

                        Span<char> newFileSpan = fileSpan[..^4].ToArray();

                        for (int i = 1; i <= 5; i++)
                            newFileSpan[^i] = char.ToLower(newFileSpan[^i]);

                        return newFileSpan.ToString();
                    }
                }

                if (OnGuessType != null) {
                    // parse custom types from mods
                    foreach (Delegate typeGuesser in OnGuessType.GetInvocationList()) {
                        string fileMod = ((TypeGuesser) typeGuesser)(file, out Type typeMod, out string formatMod);

                        if (fileMod == null || typeMod == null || formatMod == null)
                            continue;

                        file = fileMod;
                        type = typeMod;
                        format = formatMod;

                        return file;
                    }
                }

                // assign supported generic types if we haven't found a more specific one
                if (MatchExtension(fileSpan, fileNameSpan, "lua", ref warningAlreadySent, isTextBased: true)) {
                    type = typeof(AssetTypeLua);
                    return fileSpan[..^4].ToString();
                }
                if (MatchExtension(fileSpan, fileNameSpan, "txt", ref warningAlreadySent, isTextBased: true)) {
                    type = typeof(AssetTypeText);
                    return fileSpan[..^4].ToString();
                }
                if (MatchExtension(fileSpan, fileNameSpan, "xml", ref warningAlreadySent, isTextBased: true)) {
                    type = typeof(AssetTypeXml);
                    return fileSpan[..^4].ToString();
                }
                if (MatchExtension(fileSpan, fileNameSpan, "yml", ref warningAlreadySent, isTextBased: true)) {
                    type = typeof(AssetTypeYaml);
                    return fileSpan[..^4].ToString();
                }
                if (MatchExtension(fileSpan, fileNameSpan, "yaml", ref warningAlreadySent, isTextBased: true)) {
                    type = typeof(AssetTypeYaml);
                    format = "yml";
                    return fileSpan[..^5].ToString();
                }

                return fileSpan.ToString();
            }

            private static bool SpanEqualsAny(ReadOnlySpan<char> left, params string[] right) {
                foreach (string expected in right)
                    if (left.Equals(expected, StringComparison.Ordinal))
                        return true;
                return false;
            }

            private static bool SpanEquals(ReadOnlySpan<char> left, string right)
                => left.Equals(right, StringComparison.Ordinal);

            /// <summary>
            ///   Match a file extension, and log a warning if the file extension is duplicated.<br/>
            ///   If the file is text-based, log a warning if the file has an extra <c>.txt</c> extension.
            /// </summary>
            /// <param name="filePath">
            ///   The path of the file. Used when logging the warning.
            /// </param>
            /// <param name="fileName">
            ///   The file name to check, with the extensions.
            /// </param>
            /// <param name="expectedExtension">
            ///   The extension to check for, without the leading dot.
            /// </param>
            /// <param name="warningAlreadySent">
            ///   Whether a warning has already been sent about the file name extension(s).
            /// </param>
            /// <param name="isTextBased">
            ///   Whether the file is text-based, and to check for an extra <c>.txt</c> extension.
            /// </param>
            private static bool MatchExtension(
                ReadOnlySpan<char> filePath,
                ReadOnlySpan<char> fileName,
                ReadOnlySpan<char> expectedExtension,
                ref bool warningAlreadySent,
                bool isTextBased = false)
            {
                ReadOnlySpan<char> extension = Path.GetExtension(fileName);

                if (extension.IsEmpty)
                    return false;

                // remove the leading dot
                extension = extension[1..];

                if (extension.Equals(expectedExtension, StringComparison.Ordinal)) {
                    // this is silly, but it works
                    extension = Path.GetExtension(Path.GetFileNameWithoutExtension(fileName));

                    if (!warningAlreadySent && !extension.IsEmpty && extension[1..].Equals(expectedExtension, StringComparison.Ordinal)) {
                        Logger.Warn("Content", $"\"{filePath}\" has a doubled extension! It may not be handled correctly.");
                        warningAlreadySent = true;
                    }

                    return true;
                }

                if (warningAlreadySent)
                    // we don't care anymore if a warning has already been logged
                    return false;

                if (isTextBased && extension.Equals("txt", StringComparison.Ordinal)) {
                    extension = Path.GetExtension(Path.GetFileNameWithoutExtension(fileName));

                    if (!extension.IsEmpty && extension[1..].Equals(expectedExtension, StringComparison.Ordinal)) {
                        Logger.Warn("Content", $"\"{filePath}\" has an extra \".txt\" extension! It may not be handled correctly.");
                        warningAlreadySent = true;
                    }
                }

                return false;
            }

            /// <summary>
            ///   Match a multipart file extension, and log a warning if the last part of the file extension is duplicated.<br/>
            ///   If the file is text-based, log a warning if the file has an extra <c>.txt</c> extension.
            /// </summary>
            /// <param name="filePath">
            ///   The path of the file. Used when logging the warning.
            /// </param>
            /// <param name="fileName">
            ///   The file name to check, with the extensions.
            /// </param>
            /// <param name="expectedExtension">
            ///   The multipart extension to check for, without the leading dot.
            /// </param>
            /// <param name="warningAlreadySent">
            ///   Whether a warning has already been sent about the file name extension(s).
            /// </param>
            /// <param name="isTextBased">
            ///   Whether the file is text-based, and to check for an extra <c>.txt</c> extension.
            /// </param>
            private static bool MatchMultipartExtension(
                ReadOnlySpan<char> filePath,
                ReadOnlySpan<char> fileName,
                ReadOnlySpan<char> expectedExtension,
                ref bool warningAlreadySent,
                bool isTextBased = false)
            {
                // use the simpler function if this is just a singlepart extension
                if (expectedExtension.IndexOf('.') == -1)
                    return MatchExtension(filePath, fileName, expectedExtension, ref warningAlreadySent, isTextBased);

                // find all indices of '.'
                List<int> expectedExtensionDotIndices = new List<int>();
                for (int i = expectedExtension.Length - 1; i >= 0; i--)
                    if (expectedExtension[i] == '.')
                        expectedExtensionDotIndices.Add(i);

                List<int> fileNameDotIndices = new List<int>();
                for (int i = Path.GetFileName(fileName).Length - 1; i >= 0; i--)
                    if (fileName[i] == '.')
                        fileNameDotIndices.Add(i);

                if (fileNameDotIndices.Count - expectedExtensionDotIndices.Count < 1)
                    // the file name doesn't have enough extension parts to match
                    // fileName must have at least one more dot than expectedExtension
                    return false;

                // find the dot which would be where the extension should be
                int expectedExtensionIndex = fileNameDotIndices[expectedExtensionDotIndices.Count];

                // (+1 to remove the leading dot)
                if (fileName[(expectedExtensionIndex + 1)..].Equals(expectedExtension, StringComparison.Ordinal))
                    // extensions match perfectly
                    return true;

                if (warningAlreadySent)
                    // we don't care anymore if a warning has already been logged
                    return false;

                // move past the extra extension and try to match
                if ((fileNameDotIndices.Count - 1) - expectedExtensionDotIndices.Count < 1)
                    // there's no more extension parts to check for
                    return false;

                expectedExtensionIndex = fileNameDotIndices[expectedExtensionDotIndices.Count + 1];
                int actualExtensionIndex = fileNameDotIndices[0];

                // the intended extension
                ReadOnlySpan<char> intendedExtension = fileName[(expectedExtensionIndex + 1)..actualExtensionIndex];
                // the actual extension (so a .txt or a duplicate extension)
                ReadOnlySpan<char> actualExtension = fileName[(actualExtensionIndex + 1)..];
                // (+1 to remove the leading dot)

                if (intendedExtension.Equals(expectedExtension, StringComparison.Ordinal)) {
                    // there's an extra extension at the end - check whether it's a duplicated extension or
                    // an extra .txt extension if it's a text-based file - but only if we haven't warned about this one yet

                    ReadOnlySpan<char> lastIntendedExtensionPart = expectedExtension[expectedExtensionDotIndices[0]..];
                    if (actualExtension.Equals(lastIntendedExtensionPart, StringComparison.Ordinal)) {
                        Logger.Warn("Content", $"\"{filePath}\" has a doubled extension! It may not be handled correctly.");
                        warningAlreadySent = true;
                    } else if (actualExtension.Equals("txt", StringComparison.Ordinal)) {
                        Logger.Warn("Content", $"\"{filePath}\" has an extra \".txt\" extension! It may not be handled correctly.");
                        warningAlreadySent = true;
                    }
                }

                return false;
            }

            private static void RecrawlMod(ModContent mod) {
                mod.List.Clear();
                mod.Map.Clear();
                Crawl(mod);
            }

            /// <summary>
            /// Invoked when content is being updated, allowing you to handle it.
            /// </summary>
            public static event Action<ModAsset, ModAsset> OnUpdate;
            public static void Update(ModAsset prev, ModAsset next) {
                if (prev != null) {
                    foreach (object target in prev.Targets) {
                        if (target is patch_MTexture mtex) {
                            AssetReloadHelper.Do($"{Dialog.Clean("ASSETRELOADHELPER_UNLOADINGTEXTURE")} {Path.GetFileName(prev.PathVirtual)}", () => {
                                mtex.UndoOverride(prev);
                            });
                        }
                    }

                    if (next == null || prev.PathVirtual != next.PathVirtual)
                        Add(prev.PathVirtual, null);
                }


                if (next != null && TryAdd(next.PathVirtual, next)) {
                    string path = next.PathVirtual;
                    string name = Path.GetFileName(path);

                    Level levelPrev = Engine.Scene as Level ?? AssetReloadHelper.ReturnToScene as Level;

                    if (next.Type == typeof(AssetTypeMap)) {
                        string mapName = path.Substring(5);
                        ModeProperties mode =
                            AreaData.Areas
                            .SelectMany(area => area.Mode)
                            .FirstOrDefault(modeSel => modeSel?.MapData?.Filename == mapName);

                        if (mode != null) {
                            AssetReloadHelper.Do($"{Dialog.Clean("ASSETRELOADHELPER_RELOADINGMAPNAME")} {name}", _ => {
                                mode.MapData.Reload();
                                return Task.CompletedTask;
                            }).ContinueWith(_ => MainThreadHelper.Schedule(() => {
                                if (levelPrev?.Session.MapData == mode.MapData)
                                    AssetReloadHelper.ReloadLevel();
                            }));
                        } else {
                            // What can go wrong?
                            AssetReloadHelper.Do(Dialog.Clean("ASSETRELOADHELPER_RELOADINGALLMAPS"), _ => {
                                AssetReloadHelper.ReloadAllMaps();
                                return Task.CompletedTask;
                            }).ContinueWith(_ => AssetReloadHelper.ReloadLevel());
                        }

                    } else if (next.Type == typeof(AssetTypeXml) || next.Type == typeof(AssetTypeSpriteBank)) {
                        // It isn't known if the reloaded xml is part of the currently loaded level.
                        // Let's reload just to be safe.
                        AssetReloadHelper.ReloadLevel();

                    } else if (next.Type == typeof(AssetTypeTutorial)) {
                        PlaybackData.Load();
                        AssetReloadHelper.ReloadLevel();

                    } else if (next.Type == typeof(AssetTypeDecalRegistry)) {
                        DecalRegistry.LoadModDecalRegistry(next);
                        AssetReloadHelper.ReloadLevel();

                    } else if (next.Type == typeof(AssetTypeDialog) || next.Type == typeof(AssetTypeDialogExport)) {
                        AssetReloadHelper.Do($"{Dialog.Clean("ASSETRELOADHELPER_RELOADINGDIALOG")} {name}", () => {
                            string languageFilePath = path + ".txt";

                            // fix the language case if broken.
                            string languageRoot = Path.Combine(Engine.ContentDirectory, "Dialog");
                            foreach (string vanillaFilePath in patch_Dialog.GetVanillaLanguageFileList(languageRoot, "*.txt", SearchOption.AllDirectories)) {
                                if (vanillaFilePath.Equals(languageFilePath, StringComparison.InvariantCultureIgnoreCase)) {
                                    languageFilePath = vanillaFilePath;
                                    break;
                                }
                            }

                            Dialog.LoadLanguage(Path.Combine(PathContentOrig, languageFilePath));
                            patch_Dialog.RefreshLanguages();
                        });
                    } else if (next.Type == typeof(ObjModel) || next.Type == typeof(AssetTypeObjModelExport)) {
                        if (next.Type == typeof(ObjModel)) {
                            MTNExt.ObjModelCache.Remove(next.PathVirtual + ".obj");
                        } else {
                            MTNExt.ObjModelCache.Remove(next.PathVirtual + ".export");
                        }
                        MainThreadHelper.Schedule(() => MTNExt.ReloadModData());
                    } else if (next.Type == typeof(AssetTypeFont)) {
                        MainThreadHelper.Schedule(() => Fonts.Reload());
                    }

                    // Loaded assets can be folders, which means that we need to check the updated assets' entire path.
                    HashSet<WeakReference> updated = new HashSet<WeakReference>();
                    for (ModAsset asset = next; asset != null && !string.IsNullOrEmpty(asset.PathVirtual); asset = Get(Path.GetDirectoryName(asset.PathVirtual).Replace('\\', '/'))) {
                        int index = LoadedAssetPaths.IndexOf(asset.PathVirtual);
                        if (index == -1)
                            continue;

                        WeakReference weakref = LoadedAssets[index];
                        if (!updated.Add(weakref))
                            continue;

                        object target = weakref.Target;
                        if (!weakref.IsAlive)
                            continue;

                        // Don't feed the entire tree into the loaded asset, just the updated asset.
                        ProcessUpdate(target, next, false);
                    }
                }

                OnUpdate?.Invoke(prev, next);

                InvalidateInstallationHash();
            }

            /// <summary>
            /// Crawl through the content mod and automatically fill the mod asset map.
            /// </summary>
            /// <param name="meta">The content mod to crawl through.</param>
            public static void Crawl(ModContent meta) {
                if (!Mods.Contains(meta))
                    Mods.Add(meta);
                meta._Crawl();

                if (_ContentLoaded) {
                    // We're late-loading this mod and thus need to manually ingest new assets.
                    Logger.Verbose("content", $"Late ingest via update for {meta.Name}");

                    Stopwatch loadTimerPrev = Celeste.LoadTimer; // Trick AssetReloadHelper into insta-running callbacks.
                    Stopwatch loadTimer = Stopwatch.StartNew();

                    try {
                        Celeste.LoadTimer = loadTimer;
                        foreach (ModAsset asset in meta.List)
                            Update(Get(asset.PathVirtual, true), asset);
                    } finally {
                        loadTimer.Stop();
                        Celeste.LoadTimer = loadTimerPrev;
                    }
                }
            }

            /// <summary>
            /// Invoked when content is being processed (most likely on load), allowing you to manipulate it.
            /// </summary>
            public static event Action<object, string> OnProcessLoad;
            /// <summary>
            /// Process an asset and register it for further reprocessing in the future.
            /// Apply any mod-related changes to the asset based on the existing mod asset meta map.
            /// </summary>
            /// <param name="asset">The asset to process.</param>
            /// <param name="assetNameFull">The "full name" of the asset, preferable the relative asset path.</param>
            /// <returns>The processed asset.</returns>
            public static void ProcessLoad(object asset, string assetNameFull) {
                if (DumpOnLoad)
                    Dump(assetNameFull, asset);

                string assetName = assetNameFull;
                if (assetName.StartsWith(PathContentOrig)) {
                    assetName = assetName.Substring(PathContentOrig.Length + 1);
                }
                assetName = assetName.Replace('\\', '/');

                int loadedIndex = LoadedAssetPaths.IndexOf(assetName);
                if (loadedIndex == -1) {
                    LoadedAssetPaths.Add(assetName);
                    LoadedAssetFullPaths.Add(assetNameFull);
                    LoadedAssets.Add(new WeakReference(asset));
                } else {
                    LoadedAssets[loadedIndex] = new WeakReference(asset);
                }

                OnProcessLoad?.Invoke(asset, assetName);

                ProcessUpdate(asset, Get(assetName, true), true);
            }

            /// <summary>
            /// Invoked when content is being processed (most likely on load or runtime update), allowing you to manipulate it.
            /// </summary>
            public static event Action<object, ModAsset, bool> OnProcessUpdate;
            public static void ProcessUpdate(object asset, ModAsset mapping, bool load) {
                if (asset == null || mapping == null)
                    return;

                if (asset is patch_Atlas atlas) {
                    string reloadingText = Dialog.Language == null ? "" : Dialog.Clean(mapping.Children.Count == 0 ? "ASSETRELOADHELPER_RELOADINGTEXTURE" : "ASSETRELOADHELPER_RELOADINGTEXTURES");
                    AssetReloadHelper.Do(load, $"{reloadingText} {Path.GetFileName(mapping.PathVirtual)}", () => {
                        atlas.ResetCaches();
                        atlas.Ingest(mapping);
                    });

                    // if the atlas is (or contains) an emoji, register it.
                    if (Emoji.IsInitialized()) {
                        if (refreshEmojis(mapping)) {
                            MainThreadHelper.Schedule(() => {
                                Logger.Verbose("content", "Reloading fonts after late emoji registration");
                                Fonts.Reload();
                            });
                        }
                    }

                    if ((MTNExt.ModsLoaded || MTNExt.ModsDataLoaded) && potentiallyContainsMountainTextures(mapping)) {
                        AssetReloadHelper.Do(load, Dialog.Clean("ASSETRELOADHELPER_RELOADINGMOUNTAIN"), () => {
                            MTNExt.ReloadMod();
                            MainThreadHelper.Schedule(() => MTNExt.ReloadModData());
                        });
                    }
                }

                OnProcessUpdate?.Invoke(asset, mapping, load);
            }

            /// <summary>
            /// Searches for emoji in the given mod asset (recursively), returns true if at least one was found.
            /// </summary>
            private static bool refreshEmojis(ModAsset mapping) {
                if (mapping.Type == typeof(AssetTypeDirectory)) {
                    lock (mapping.Children) {
                        bool emojiFilled = false;
                        foreach (ModAsset child in mapping.Children) {
                            emojiFilled = refreshEmojis(child) || emojiFilled;
                        }
                        return emojiFilled;
                    }
                } else if (mapping.PathVirtual.StartsWith("Graphics/Atlases/Gui/emoji/")) {
                    string emojiName = mapping.PathVirtual.Substring(27);
                    Logger.Verbose("content", $"Late registering emoji: {emojiName}");
                    Emoji.Register(emojiName, GFX.Gui["emoji/" + emojiName]);
                    return true;
                }
                return false;
            }

            private static bool potentiallyContainsMountainTextures(ModAsset mapping) {
                if (mapping.Type == typeof(AssetTypeDirectory)) {
                    lock (mapping.Children) {
                        foreach (ModAsset child in mapping.Children) {
                            if (potentiallyContainsMountainTextures(child)) {
                                return true;
                            }
                        }
                        return false;
                    }
                }
                return mapping.PathVirtual.StartsWith("Graphics/Atlases/Mountain/");
            }

            /// <summary>
            /// Dump all dumpable game content into PathDUMP.
            /// </summary>
            public static void DumpAll() {
                bool prevDumpOnLoad = DumpOnLoad;
                DumpOnLoad = true;
                // TODO: Load and dump all other assets in original Content directory.

                // Dump atlases.

                // Noel on Discord:
                // not using it for the celeste assets but the "crunch" atlas packer is open source: https://github.com/ChevyRay/crunch
                // all celeste graphic assets use the Packer or PackerNoAtlas one tho

                // TODO: Find how to differentiate between Packer and PackerNoAtlas
                foreach (string file in Directory.EnumerateFiles(Path.Combine(PathContentOrig, "Graphics", "Atlases"), "*.meta", SearchOption.AllDirectories)) {
                    Logger.Verbose("dump-all-atlas-meta", "file: " + file);
                    // THIS IS HORRIBLE.
                    try {
                        Atlas.FromAtlas(file.Substring(0, file.Length - 5), Atlas.AtlasDataFormat.Packer).Dispose();
                    } catch {
                        Atlas.FromAtlas(file.Substring(0, file.Length - 5), Atlas.AtlasDataFormat.PackerNoAtlas).Dispose();
                    }
                }

                DumpOnLoad = prevDumpOnLoad;
            }

            /// <summary>
            /// Dump the given asset into an user-friendly and mod-compatible format.
            /// </summary>
            /// <param name="assetNameFull">The "full name" of the asset, preferable the relative asset path.</param>
            /// <param name="asset">The asset to process.</param>
            public static void Dump(string assetNameFull, object asset) {
                string assetName = assetNameFull;
                if (assetName.StartsWith(PathContentOrig)) {
                    assetName = assetName.Substring(PathContentOrig.Length + 1);
                } else if (File.Exists(assetName))
                    return; // Don't dump absolutely loaded files.

                string pathDump = Path.Combine(PathDUMP, assetName);
                Directory.CreateDirectory(Path.GetDirectoryName(pathDump));

                Logger.Verbose("dump", $"{assetNameFull} {asset.GetType().FullName}");

                if (asset is IMeta) {
                    if (!File.Exists(pathDump + ".meta.yaml"))
                        using (Stream stream = File.OpenWrite(pathDump + ".meta.yaml"))
                        using (StreamWriter writer = new StreamWriter(stream))
                            YamlHelper.Serializer.Serialize(writer, asset, asset.GetType());

                } else if (asset is Texture2D tex) {
                    if (!File.Exists(pathDump + ".png"))
                        using (Stream stream = File.OpenWrite(pathDump + ".png"))
                            tex.SaveAsPng(stream, tex.Width, tex.Height);

                } else if (asset is VirtualTexture vtex) {
                    Dump(assetName, vtex.Texture);

                } else if (asset is MTexture mtex) {
                    // Always copy even if !.IsSubtexture() as we need to Postdivide()
                    using (Texture2D region = mtex.GetPaddedSubtextureCopy().Postdivide())
                        Dump(assetName, region);

                    /*/
                    if (mtex.DrawOffset.X != 0 || mtex.DrawOffset.Y != 0 ||
                        mtex.Width != mtex.ClipRect.Width || mtex.Height != mtex.ClipRect.Height
                    ) {
                        Dump(assetName, new MTextureMeta {
                            X = (int) Math.Round(mtex.DrawOffset.X),
                            Y = (int) Math.Round(mtex.DrawOffset.Y),
                            Width = mtex.Width,
                            Height = mtex.Height
                        });
                    }
                    /**/

                } else if (asset is patch_Atlas atlas) {

                    /*
                    for (int i = 0; i < atlas.Sources.Count; i++) {
                        VirtualTexture source = atlas.Sources[i];
                        string name = source.Name;

                        if (name.StartsWith(assetNameFull))
                            name = assetName + "_s_" + name.Substring(assetNameFull.Length);
                        else
                            name = Path.Combine(assetName + "_s", name);
                        if (name.EndsWith(".data") || name.EndsWith(".meta"))
                            name = name.Substring(0, name.Length - 5);

                        Dump(name, source);
                    }
                    */

                    Dictionary<string, MTexture> textures = atlas.Textures;
                    foreach (KeyValuePair<string, MTexture> kvp in textures) {
                        string name = kvp.Key;
                        MTexture source = kvp.Value;
                        Dump(Path.Combine(assetName, name.Replace('/', Path.DirectorySeparatorChar)), source);
                    }
                }

                // TODO: Dump more asset types if required.
            }

        }
    }
}
