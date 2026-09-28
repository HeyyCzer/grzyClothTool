using CodeWalker.GameFiles;
using CodeWalker.GameFiles;
using CodeWalker.Utils;
using grzyClothTool.Constants;
using grzyClothTool.Models;
using grzyClothTool.Models.Drawable;
using GTexture = grzyClothTool.Models.Texture.GTexture;
using grzyClothTool.Views;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using static grzyClothTool.Enums;

namespace grzyClothTool.Helpers;

public class BuildResourceHelper
{
    private Addon _addon;
    private int _number;
    private readonly string _projectName;
    private string _buildPath;
    private readonly string _baseBuildPath;
    private readonly bool _splitAddons;
    private readonly BuildReporter _reporter;

    // Shared by every addon/sex task of this build so parallel work never exceeds the machine.
    private readonly int _maxParallelism;
    private readonly SemaphoreSlim _cpuLimiter;
    private readonly SemaphoreSlim _ioLimiter;
    private readonly MemoryBudget _memoryBudget;
    private readonly CancellationToken _cancellationToken;

    private readonly string _buildTempFolderPath;
    private readonly bool shouldUseNumber = false;

    private readonly List<string> firstPersonFiles = []; // guarded by lock (firstPersonFiles)
    private BuildResourceType _buildResourceType;

    public BuildResourceHelper(string name, string path, BuildResourceType resourceType, bool splitAddons, BuildReporter reporter = null, int? maxParallelism = null, CancellationToken cancellationToken = default)
    {
        _cancellationToken = cancellationToken;
        _projectName = name;
        _buildPath = path;
        _baseBuildPath = path; // store base build path, as we will be modyfiyng _buildPath, when splitting addons
        _reporter = reporter ?? new BuildReporter();
        _maxParallelism = Math.Max(1, maxParallelism ?? GetDefaultParallelism());
        _cpuLimiter = new SemaphoreSlim(_maxParallelism, _maxParallelism);
        _ioLimiter = new SemaphoreSlim(IoParallelism, IoParallelism);
        _memoryBudget = new MemoryBudget(MemoryBudget.GetDefaultCapacity());
        _buildResourceType = resourceType;
        _splitAddons = splitAddons;

        shouldUseNumber = MainWindow.AddonManager.Addons.Count > 1;

        var buildParentDir = Path.GetDirectoryName(_baseBuildPath);
        _buildTempFolderPath = Path.Combine(buildParentDir, "buildtemp");
        Directory.CreateDirectory(_buildTempFolderPath);
    }

    private const int IoParallelism = 8;

    // One worker per core (minus one for the UI); memory is bounded separately by _memoryBudget.
    public static int GetDefaultParallelism() => Math.Max(1, Environment.ProcessorCount - 1);

    // Rough peak bytes while one texture is processed: managed copies of the source (file, decompressed
    // resource, pixel data) plus ImageMagick's Q8 pixel cache for the source and the resized image.
    private const int TextureBytesPerPixelEstimate = 4 * 5;
    private const int DefaultTextureSize = 2048;

    private static long EstimateTextureCost(GTexture t)
    {
        long width = t.TxtDetails?.Width > 0 ? t.TxtDetails.Width : DefaultTextureSize;
        long height = t.TxtDetails?.Height > 0 ? t.TxtDetails.Height : DefaultTextureSize;
        return width * height * TextureBytesPerPixelEstimate;
    }

    // A YDD resave holds the whole file (compressed + decompressed) and re-encodes its embedded textures.
    private static long EstimateYddCost(GDrawable d)
    {
        try
        {
            return Math.Max(new FileInfo(d.FullFilePath).Length * 8, 64L * 1024 * 1024);
        }
        catch (Exception)
        {
            return 64L * 1024 * 1024;
        }
    }

    /// <summary>
    /// Total weighted work of a build, matching what the build reports through <see cref="BuildReporter.Complete"/>.
    /// </summary>
    public static long EstimateWork(IEnumerable<Addon> addons, BuildResourceType resourceType)
    {
        long total = 0;
        foreach (var drawable in addons.SelectMany(a => a.Drawables))
        {
            total += GetYddWeight(drawable);
            foreach (var texture in drawable.Textures)
            {
                total += GetTextureWeight(texture, resourceType);
            }
        }
        return total;
    }

    private static long GetYddWeight(GDrawable drawable) =>
        NeedsYddResave(drawable) ? BuildReporter.WeightYddResave : BuildReporter.WeightCopy;

    private static long GetTextureWeight(GTexture texture, BuildResourceType resourceType)
    {
        if (texture.IsOptimizedDuringBuild)
        {
            return BuildReporter.WeightTextureOptimize;
        }

        // FiveM converts jpg/png/dds sources to .ytd; AltV and Singleplayer copy them as they are.
        return resourceType == BuildResourceType.FiveM && texture.Extension != ".ytd"
            ? BuildReporter.WeightTextureConvert
            : BuildReporter.WeightCopy;
    }

    private static bool NeedsYddResave(GDrawable dr)
    {
        if (dr?.IsEncrypted == true || dr.Details?.EmbeddedTextures == null || dr.Details.EmbeddedTextures.Count == 0 || dr.Details.EmbeddedTextures.All(x => x.Value.Details.Width == 0))
        {
            return false;
        }

        return dr.Details.EmbeddedTextures.Any(kvp =>
        {
            var embeddedDto = kvp.Value;
            return embeddedDto.IsOptimizedDuringBuild || embeddedDto.HasReplacement || embeddedDto.OriginalName != embeddedDto.Details.Name;
        });
    }

    private void StartBuild(BuildResourceType resourceType)
    {
        _reporter.Start(EstimateWork(MainWindow.AddonManager.Addons, resourceType));
        _reporter.SetPhase($"Building {resourceType} resource with {_maxParallelism} parallel worker(s), {_memoryBudget.Capacity / (1024 * 1024)} MB memory budget");
    }

    private string GetScope(SexType sex) => $"{_addon?.Name ?? GetProjectName()}/{sex}";

    public void SetAddon(Addon addon)
    {
        _addon = addon;
    }

    public void SetNumber(int number)
    {
        _number = number;
    }

    public void UpdateBuildPath()
    {
        _buildPath = Path.Combine(_baseBuildPath, GetProjectName());
    }

    public string GetProjectName(int? number = null)
    {
        number ??= _number;
        return shouldUseNumber ? $"{_projectName}_{number:D2}" : _projectName;
    }
    

    #region FiveM


    private async Task BuildFiveMFilesAsync(SexType sex, byte[] ymtBytes, int counter)
    {
        var pedName = GetPedName(sex);
        var projectName = GetProjectName(counter);
        var scope = GetScope(sex);

        var drawables = _addon.Drawables.Where(x => x.Sex == sex).ToList();
        var drawableGroups = drawables.Select((x, i) => new { Index = i, Value = x })
                                       .GroupBy(x => x.Value.Number / GlobalConstants.MAX_DRAWABLES_IN_ADDON)
                                       .Select(x => x.Select(v => v.Value).OrderBy(d => d.Number).ToList())
                                       .ToList();

        var streamDirectory = Path.Combine(_buildPath, "stream");
        Directory.CreateDirectory(streamDirectory);
        
        var yddPathsDict = await BatchResaveYdd(drawables, scope);
        
        var fileOperations = new List<Task>();
        
        var ymtPath = Path.Combine(streamDirectory, $"{pedName}_{projectName}.ymt");
        fileOperations.Add(File.WriteAllBytesAsync(ymtPath, ymtBytes));

        foreach (var group in drawableGroups)
        {
            foreach (var d in group)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var tempYddPath = yddPathsDict[d];

                var drawablePedName = d.IsProp ? $"{pedName}_p" : pedName;
                
                var folderPath = SimplePathBuilder.BuildPath(d, _buildPath, _buildResourceType);
                Directory.CreateDirectory(folderPath);
                
                var prefix = RemoveInvalidChars($"{drawablePedName}_{projectName}^");
                var finalPath = Path.Combine(folderPath, $"{prefix}{d.Name}{Path.GetExtension(d.FullFilePath)}");
                fileOperations.Add(CopyExtraFileAsync(tempYddPath, finalPath));

                if (!string.IsNullOrEmpty(d.ClothPhysicsPath))
                {
                    fileOperations.Add(CopyExtraFileAsync(d.FullClothPhysicsPath, Path.Combine(folderPath, $"{prefix}{d.Name}{Path.GetExtension(d.ClothPhysicsPath)}")));
                }

                if (!string.IsNullOrEmpty(d.FirstPersonPath))
                {
                    //todo: this probably shouldn't be hardcoded to "_1", handle it when there is option to add more alternate drawable versions
                    fileOperations.Add(CopyExtraFileAsync(d.FullFirstPersonPath, Path.Combine(folderPath, $"{prefix}{d.Name}_1{Path.GetExtension(d.FirstPersonPath)}")));
                    
                    var name = $"{prefix}{d.Name}".Replace("^", "/");
                    AddFirstPersonFile(name);
                }

                foreach (var t in d.Textures)
                {
                    var buildName = RemoveInvalidChars(t.GetBuildName());
                    var finalTexPath = Path.Combine(folderPath, $"{prefix}{buildName}.ytd");

                    // jpg/png/dds sources are converted to .ytd; .ytd files are copied straight from disk
                    fileOperations.Add(WriteTextureFileAsync(t, finalTexPath, scope, convertNonYtd: true, overwrite: true));
                }
            }
        }

        await Task.WhenAll(fileOperations);

        await Task.Run(() =>
            {
                var generated = GenerateCreatureMetadata(drawables);
                generated?.Save(streamDirectory + "/mp_creaturemetadata_" + GetGenderLetter(sex) + "_" + projectName + ".ymt");
            }
        );
    }

    public async Task BuildFiveMResource()
    {
        StartBuild(BuildResourceType.FiveM);
        CleanupBuildOutputDirectory();

        int counter = 1;
        var metaFiles = new List<string>();
        var tasks = new List<Task>();


        // this could be merged into one if, but I prefer to keep it separate for readability
        if (_splitAddons)
        {
            foreach (var selectedAddon in MainWindow.AddonManager.Addons)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                SetAddon(selectedAddon);
                SetNumber(counter);
                UpdateBuildPath();

                AddBuildTasksForSex(selectedAddon, SexType.male, tasks, metaFiles, counter);
                AddBuildTasksForSex(selectedAddon, SexType.female, tasks, metaFiles, counter);

                await Task.WhenAll(tasks);
                _reporter.SetPhase($"Writing manifests for {selectedAddon.Name}");
                BuildFirstPersonAlternatesMeta();
                BuildPedAlternativeVariationsMeta();
                BuildFxManifest(metaFiles);

                counter++;

                // we don't have to clear firstpersonalternates meta files, because they are cleared in BuildFxManifest
                metaFiles.Clear();
                tasks.Clear();
            }
        } 
        else
        {
            foreach (var selectedAddon in MainWindow.AddonManager.Addons)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                SetAddon(selectedAddon);
                SetNumber(counter);

                AddBuildTasksForSex(selectedAddon, SexType.male, tasks, metaFiles, counter);
                AddBuildTasksForSex(selectedAddon, SexType.female, tasks, metaFiles, counter);

                counter++;
            }

            await Task.WhenAll(tasks);
            _reporter.SetPhase("Writing manifests");
            BuildFirstPersonAlternatesMeta();
            BuildPedAlternativeVariationsMeta();
            BuildFxManifest(metaFiles);
        }

        CleanupBuildTempDirectory();
    }

    private void BuildFxManifest(List<string> metaFiles)
    {
        StringBuilder contentBuilder = new();
        contentBuilder.AppendLine("-- This resource was generated by grzyClothTool :)");
        contentBuilder.AppendLine($"-- {GlobalConstants.DISCORD_INVITE_URL}");
        contentBuilder.AppendLine();
        contentBuilder.AppendLine("fx_version 'cerulean'");
        contentBuilder.AppendLine("game 'gta5'");
        contentBuilder.AppendLine("author 'grzyClothTool'");
        contentBuilder.AppendLine();
        contentBuilder.AppendLine("files {");

        var filesSectionBuilder = new StringBuilder();
        filesSectionBuilder.Append(string.Join(",\n  ", metaFiles.Select(f => $"'{f}'")));

        if (firstPersonFiles.Count > 0)
        {
            filesSectionBuilder.Append($",\n  'first_person_alternates_{_projectName}.meta'");
        }

        var projectName = GetProjectName();
        var pedAltVarPath = Path.Combine(_buildPath, $"pedalternativevariations_{projectName}.meta");
        var hasPedAltVar = File.Exists(pedAltVarPath);
        if (hasPedAltVar)
        {
            filesSectionBuilder.Append($",\n  'pedalternativevariations_{projectName}.meta'");
        }

        contentBuilder.AppendLine($"  {filesSectionBuilder}");

        contentBuilder.AppendLine("}");
        contentBuilder.AppendLine();

        foreach (var file in metaFiles)
        {
            contentBuilder.AppendLine($"data_file 'SHOP_PED_APPAREL_META_FILE' '{file}'");
        }

        if (firstPersonFiles.Count > 0)
        {
            contentBuilder.AppendLine($"data_file 'PED_FIRST_PERSON_ALTERNATE_DATA' 'first_person_alternates_{_projectName}.meta'");
        }

        if (hasPedAltVar)
        {
            contentBuilder.AppendLine($"data_file 'ALTERNATE_VARIATIONS_FILE' 'pedalternativevariations_{projectName}.meta'");
        }

        var finalPath = Path.Combine(_buildPath, "fxmanifest.lua");
        File.WriteAllText(finalPath, contentBuilder.ToString());

        //clear firstPerson files
        firstPersonFiles.Clear();
    }

    #endregion

    #region AltV

    public async Task BuildAltVResource()
    {
        StartBuild(BuildResourceType.AltV);
        CleanupBuildOutputDirectory();

        int counter = 1;
        var metaFiles = new List<string>();
        var tasks = new List<Task>();

        if (_splitAddons)
        {
            foreach (var selectedAddon in MainWindow.AddonManager.Addons)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                SetAddon(selectedAddon);
                SetNumber(counter);
                UpdateBuildPath();

                AddBuildTasksForSex(selectedAddon, SexType.male, tasks, metaFiles, counter);
                AddBuildTasksForSex(selectedAddon, SexType.female, tasks, metaFiles, counter);

                counter++;

                await Task.WhenAll(tasks);
                _reporter.SetPhase($"Writing manifests for {selectedAddon.Name}");
                BuildAltVTomls(metaFiles);

                metaFiles.Clear();
                tasks.Clear();
            }
        }
        else
        {
            foreach (var selectedAddon in MainWindow.AddonManager.Addons)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                SetAddon(selectedAddon);
                SetNumber(counter);

                AddBuildTasksForSex(selectedAddon, SexType.male, tasks, metaFiles, counter);
                AddBuildTasksForSex(selectedAddon, SexType.female, tasks, metaFiles, counter);

                counter++;
            }

            await Task.WhenAll(tasks);
            _reporter.SetPhase("Writing manifests");
            BuildAltVTomls(metaFiles);
        }

        CleanupBuildTempDirectory();
    }

    private async Task BuildAltVFilesAsync(SexType sex, byte[] ymtBytes, int counter)
    {
        var pedName = GetPedName(sex);
        var projectName = GetProjectName(counter);
        var scope = GetScope(sex);

        var drawables = _addon.Drawables.Where(x => x.Sex == sex).ToList();
        var drawableGroups = drawables.Select((x, i) => new { Index = i, Value = x })
                                       .GroupBy(x => x.Value.Number / GlobalConstants.MAX_DRAWABLES_IN_ADDON)
                                       .Select(x => x.Select(v => v.Value).ToList())
                                       .ToList();

        // Prepare all directory paths first to minimize file system access
        var genderFolderName = sex == SexType.male ? $"{projectName}_male.rpf" : $"{projectName}_female.rpf";

        //Describe the 3 levels of a AltV clothes resource
        var firstLevelFolder = Path.Combine(_buildPath, "stream");
        var secondLevelFolder = Path.Combine(_buildPath, "stream", genderFolderName);
        var thirdLevelFolder = Path.Combine(_buildPath, "stream", genderFolderName, $"{pedName}_{projectName}");

        var directoriesToEnsure = new List<string> {
            firstLevelFolder, //First level is the stream folder
            secondLevelFolder,
            thirdLevelFolder
        };

        string thirdLevelPropFolder = null;
        //Additionaly if props are present, create a folder for them
        if(drawables.Any(x => x.IsProp)) {
            var genderPropFolderName = sex == SexType.male ? $"{projectName}_male_p.rpf" : $"{projectName}_female_p.rpf";

            var secondLevelPropFolder = Path.Combine(_buildPath, "stream", genderPropFolderName);
            thirdLevelPropFolder = Path.Combine(_buildPath, "stream", genderPropFolderName, $"{pedName}_p_{projectName}");
            
            directoriesToEnsure.Add(secondLevelPropFolder);
            directoriesToEnsure.Add(thirdLevelPropFolder);
        }

        foreach(var dir in directoriesToEnsure) {
            Directory.CreateDirectory(dir);
        }

        var yddPathsDict = await BatchResaveYdd(drawables, scope);

        var fileOperations = new List<Task>();

        foreach(var group in drawableGroups) {
            var ymtPath = Path.Combine(secondLevelFolder, $"{pedName}_{projectName}.ymt");
            fileOperations.Add(File.WriteAllBytesAsync(ymtPath, ymtBytes));

            foreach(var d in group) {
                _cancellationToken.ThrowIfCancellationRequested();
                var folderPath = d.IsProp ? thirdLevelPropFolder : thirdLevelFolder;

                var tempYddPath = yddPathsDict[d];
                fileOperations.Add(CopyExtraFileAsync(tempYddPath, Path.Combine(folderPath, $"{d.Name}{Path.GetExtension(d.FullFilePath)}")));

                foreach(var t in d.Textures)
                {
                    var buildName = RemoveInvalidChars(t.GetBuildName());
                    var finalTexPath = Path.Combine(folderPath, $"{buildName}{Path.GetExtension(t.FullFilePath)}");

                    // AltV keeps non-optimized textures as they are (no ytd conversion)
                    fileOperations.Add(WriteTextureFileAsync(t, finalTexPath, scope, convertNonYtd: false, overwrite: false));
                }
            }
        }

        await Task.WhenAll(fileOperations);

        await Task.Run(() => {
            var generated = GenerateCreatureMetadata(drawables);
            if (generated != null)
            {
                string creatureMetadataFolder = Path.Combine(firstLevelFolder, "creaturemetadata.rpf");
                Directory.CreateDirectory(creatureMetadataFolder);
                generated.Save(Path.Combine(creatureMetadataFolder, $"mp_creaturemetadata_{GetGenderLetter(sex)}_{projectName}.ymt"));
            }
        });
    }

    private void BuildAltVTomls(List<string> metaFiles)
    {
        StringBuilder resourceTomlBuilder = new();
        resourceTomlBuilder.AppendLine("# This resource was generated by grzyClothTool :)");
        resourceTomlBuilder.AppendLine($"# {GlobalConstants.DISCORD_INVITE_URL}");
        resourceTomlBuilder.AppendLine();
        resourceTomlBuilder.AppendLine("type = 'dlc'");
        resourceTomlBuilder.AppendLine("main = 'stream.toml'");
        resourceTomlBuilder.AppendLine("client-files = [ 'stream/*' ]");

        var finalResourceTomlPath = Path.Combine(_buildPath, "resource.toml");
        File.WriteAllText(finalResourceTomlPath, resourceTomlBuilder.ToString());

        StringBuilder streamTomlBuilder = new();
        streamTomlBuilder.AppendLine("# This resource was generated by grzyClothTool :)");
        streamTomlBuilder.AppendLine($"# {GlobalConstants.DISCORD_INVITE_URL}");
        streamTomlBuilder.AppendLine();
        streamTomlBuilder.AppendLine("files = [");
        streamTomlBuilder.AppendLine("'stream/*',");
        streamTomlBuilder.AppendLine("]");
        streamTomlBuilder.AppendLine();
        streamTomlBuilder.AppendLine("[meta]");

        foreach(var file in metaFiles) {
            streamTomlBuilder.AppendLine($"'stream/{file}' = 'SHOP_PED_APPAREL_META_FILE'");
        }

        var finalStreamTomlPath = Path.Combine(_buildPath, "stream.toml");
        File.WriteAllText(finalStreamTomlPath, streamTomlBuilder.ToString());
    }

    #endregion


    #region Singleplayer

    public async Task BuildSingleplayerResource()
    {
        StartBuild(BuildResourceType.Singleplayer);
        string dlcRpfPath = Path.Combine(_buildPath, "dlc.rpf");
        if (File.Exists(dlcRpfPath))
        {
            try
            {
                File.Delete(dlcRpfPath);
            }
            catch (Exception ex)
            {
                _reporter.Warning($"Failed to delete existing dlc.rpf: {ex.Message}");
            }
        }

        var dlcRpf = RpfFile.CreateNew(_buildPath, "dlc.rpf", RpfEncryption.OPEN);
        var creatureMetadatas = BuildContentXml(dlcRpf.Root);
        BuildSetupXml(dlcRpf.Root);

        var x64 = RpfFile.CreateDirectory(dlcRpf.Root, "x64");
        var common = RpfFile.CreateDirectory(dlcRpf.Root, "common");
        var dataFolder = RpfFile.CreateDirectory(common, "data");

        var models = RpfFile.CreateDirectory(x64, "models");
        var cdimages = RpfFile.CreateDirectory(models, "cdimages");

        if (creatureMetadatas.Count > 0)
        {
            var animFolder = RpfFile.CreateDirectory(x64, "anim");
            var creature = RpfFile.CreateNew(animFolder, "creaturemetadata.rpf");

            foreach (var meta in creatureMetadatas)
            {
                RpfFile.CreateFile(creature.Root, meta.SingleplayerFileName + ".ymt", meta.Save());
            }
        }

        int counter = 1;
        
        foreach (var selectedAddon in MainWindow.AddonManager.Addons)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            SetAddon(selectedAddon);
            SetNumber(counter);

            if (selectedAddon.HasSex(SexType.male))
            {
                var bytes = BuildYMT(SexType.male);
                var (name, metaBytes) = BuildMeta(SexType.male);
                RpfFile.CreateFile(dataFolder, name, metaBytes);
                await BuildSingleplayerFilesAsync(SexType.male, bytes, counter, cdimages);
            }

            if (selectedAddon.HasSex(SexType.female))
            {
                var bytes = BuildYMT(SexType.female);
                var (name, metaBytes) = BuildMeta(SexType.female);
                RpfFile.CreateFile(dataFolder, name, metaBytes);
                await BuildSingleplayerFilesAsync(SexType.female, bytes, counter, cdimages);
            }

            counter++;
        }

        _reporter.SetPhase("Cleaning up");
        CleanupBuildTempDirectory();
    }

    private List<RbfFile> BuildContentXml(RpfDirectoryEntry dir)
    {
        StringBuilder sb = new();

        sb.AppendLine($"<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine($"<CDataFileMgr__ContentsOfDataFileXml>");
        sb.AppendLine($"  <disabledFiles />");
        sb.AppendLine($"  <includedXmlFiles />");
        sb.AppendLine($"  <includedDataFiles />");
        sb.AppendLine($"  <dataFiles>");

        var generatedCreatureMetadatas = new List<RbfFile>();
        var filesToEnable = new List<string>();
        foreach (var addon in MainWindow.AddonManager.Addons)
        {
            if (addon.HasSex(SexType.male))
            {
                sb.AppendLine($"    <Item>");
                sb.AppendLine($"      <filename>dlc_{_projectName}:/common/data/mp_m_freemode_01_{_projectName}.meta</filename>");
                sb.AppendLine($"      <fileType>SHOP_PED_APPAREL_META_FILE</fileType>");
                sb.AppendLine($"      <overlay value=\"false\" />");
                sb.AppendLine($"      <disabled value=\"true\" />");
                sb.AppendLine($"      <persistent value=\"false\" />");
                sb.AppendLine($"    </Item>");

                sb.AppendLine($"    <Item>");
                sb.AppendLine($"      <filename>dlc_{_projectName}:/%PLATFORM%/models/cdimages/{_projectName}_male.rpf</filename>");
                sb.AppendLine($"      <fileType>RPF_FILE</fileType>");
                sb.AppendLine($"      <overlay value=\"false\" />");
                sb.AppendLine($"      <disabled value=\"true\" />");
                sb.AppendLine($"      <persistent value=\"true\" />");
                sb.AppendLine($"    </Item>");

                filesToEnable.Add($"dlc_{_projectName}:/common/data/mp_m_freemode_01_{_projectName}.meta");
                filesToEnable.Add($"dlc_{_projectName}:/%PLATFORM%/models/cdimages/{_projectName}_male.rpf");

                if (addon.HasProps())
                {
                    sb.AppendLine($"    <Item>");
                    sb.AppendLine($"      <filename>dlc_{_projectName}:/%PLATFORM%/models/cdimages/{_projectName}_male_p.rpf</filename>");
                    sb.AppendLine($"      <fileType>RPF_FILE</fileType>");
                    sb.AppendLine($"      <overlay value=\"false\" />");
                    sb.AppendLine($"      <disabled value=\"true\" />");
                    sb.AppendLine($"      <persistent value=\"true\" />");
                    sb.AppendLine($"    </Item>");

                    filesToEnable.Add($"dlc_{_projectName}:/%PLATFORM%/models/cdimages/{_projectName}_male_p.rpf");
                }

                var drawables = addon.Drawables.Where(x => x.Sex == SexType.male).ToList();
                var generated = GenerateCreatureMetadata(drawables);
                if (generated != null)
                {
                    generated.SingleplayerFileName = $"mp_creaturemetadata_m_{_projectName}";
                    generatedCreatureMetadatas.Add(generated);
                }
            }

            if (addon.HasSex(SexType.female))
            {
                sb.AppendLine($"    <Item>");
                sb.AppendLine($"      <filename>dlc_{_projectName}:/common/data/mp_f_freemode_01_{_projectName}.meta</filename>");
                sb.AppendLine($"      <fileType>SHOP_PED_APPAREL_META_FILE</fileType>");
                sb.AppendLine($"      <overlay value=\"false\" />");
                sb.AppendLine($"      <disabled value=\"true\" />");
                sb.AppendLine($"      <persistent value=\"false\" />");
                sb.AppendLine($"    </Item>");

                sb.AppendLine($"    <Item>");
                sb.AppendLine($"      <filename>dlc_{_projectName}:/%PLATFORM%/models/cdimages/{_projectName}_female.rpf</filename>");
                sb.AppendLine($"      <fileType>RPF_FILE</fileType>");
                sb.AppendLine($"      <overlay value=\"false\" />");
                sb.AppendLine($"      <disabled value=\"true\" />");
                sb.AppendLine($"      <persistent value=\"true\" />");
                sb.AppendLine($"    </Item>");

                filesToEnable.Add($"dlc_{_projectName}:/common/data/mp_f_freemode_01_{_projectName}.meta");
                filesToEnable.Add($"dlc_{_projectName}:/%PLATFORM%/models/cdimages/{_projectName}_female.rpf");

                if (addon.HasProps())
                {
                    sb.AppendLine($"    <Item>");
                    sb.AppendLine($"      <filename>dlc_{_projectName}:/%PLATFORM%/models/cdimages/{_projectName}_female_p.rpf</filename>");
                    sb.AppendLine($"      <fileType>RPF_FILE</fileType>");
                    sb.AppendLine($"      <overlay value=\"false\" />");
                    sb.AppendLine($"      <disabled value=\"true\" />");
                sb.AppendLine($"      <persistent value=\"true\" />");
                    sb.AppendLine($"    </Item>");

                    filesToEnable.Add($"dlc_{_projectName}:/%PLATFORM%/models/cdimages/{_projectName}_female_p.rpf");
                }

                var drawables = addon.Drawables.Where(x => x.Sex == SexType.female).ToList();
                var generated = GenerateCreatureMetadata(drawables);
                if (generated != null)
                {
                    generated.SingleplayerFileName = $"mp_creaturemetadata_f_{_projectName}";
                    generatedCreatureMetadatas.Add(generated);
                }
            }
        }

        if (generatedCreatureMetadatas.Count > 0)
        {
            sb.AppendLine($"    <Item>");
            sb.AppendLine($"      <filename>dlc_{_projectName}:/%PLATFORM%/anim/creaturemetadata.rpf</filename>");
            sb.AppendLine($"      <fileType>RPF_FILE</fileType>");
            sb.AppendLine($"      <overlay value=\"false\" />");
            sb.AppendLine($"      <disabled value=\"true\" />");
            sb.AppendLine($"      <persistent value=\"true\" />");
            sb.AppendLine($"    </Item>");

            filesToEnable.Add($"dlc_{_projectName}:/%PLATFORM%/anim/creaturemetadata.rpf");
        }

        sb.AppendLine($"  </dataFiles>");
        sb.AppendLine($"  <contentChangeSets>");
        sb.AppendLine($"    <Item>");
        sb.AppendLine($"      <changeSetName>{_projectName.ToUpper()}_GEN</changeSetName>");
        sb.AppendLine($"      <mapChangeSetData />");
        sb.AppendLine($"      <filesToInvalidate />");
        sb.AppendLine($"      <filesToDisable />");
        sb.AppendLine($"	  <filesToEnable>");

        foreach (var file in filesToEnable)
        {
            sb.AppendLine($"	    <Item>{file}</Item>");
        }

        sb.AppendLine($"	  </filesToEnable>");
        sb.AppendLine($"      <txdToLoad />");
        sb.AppendLine($"      <txdToUnload />");
        sb.AppendLine($"      <residentResources />");
        sb.AppendLine($"      <unregisterResources />");
        sb.AppendLine($"      <requiresLoadingScreen value=\"false\" />");
        sb.AppendLine($"    </Item>");
        sb.AppendLine($"  </contentChangeSets>");
        sb.AppendLine($"  <patchFiles />");
        sb.AppendLine($"</CDataFileMgr__ContentsOfDataFileXml>");

        RpfFile.CreateFile(dir, "content.xml", Encoding.UTF8.GetBytes(sb.ToString()));

        return generatedCreatureMetadatas;
    }

    private void BuildSetupXml(RpfDirectoryEntry dir)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("dd/MM/yyyy HH:mm:ss");

        StringBuilder sb = new();
        sb.AppendLine($"<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine($"<SSetupData>");
        sb.AppendLine($"  <deviceName>dlc_{_projectName}</deviceName>");
        sb.AppendLine($"  <datFile>content.xml</datFile>");
        sb.AppendLine($"  <timeStamp>{timestamp}</timeStamp>");
        sb.AppendLine($"  <nameHash>{_projectName}</nameHash>");
        sb.AppendLine($"  <contentChangeSets />");
        sb.AppendLine($"  <contentChangeSetGroups>");
        sb.AppendLine($"    <Item>");
        sb.AppendLine($"      <NameHash>GROUP_STARTUP</NameHash>");
        sb.AppendLine($"      <ContentChangeSets>");
        sb.AppendLine($"        <Item>{_projectName.ToUpper()}_GEN</Item>");
        sb.AppendLine($"      </ContentChangeSets>");
        sb.AppendLine($"    </Item>");
        sb.AppendLine($"  </contentChangeSetGroups>");
        sb.AppendLine($"  <startupScript />");
        sb.AppendLine($"  <scriptCallstackSize value=\"0\" />");
        sb.AppendLine($"  <type>EXTRACONTENT_COMPAT_PACK</type>");
        sb.AppendLine($"  <order value=\"999\" />");
        sb.AppendLine($"  <minorOrder value=\"0\" />");
        sb.AppendLine($"  <isLevelPack value=\"false\" />");
        sb.AppendLine($"  <dependencyPackHash />");
        sb.AppendLine($"  <requiredVersion />");
        sb.AppendLine($"  <subPackCount value=\"0\" />");
        sb.AppendLine($"</SSetupData>");

        RpfFile.CreateFile(dir, "setup2.xml", Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private async Task BuildSingleplayerFilesAsync(SexType sex, byte[] ymtBytes, int counter, RpfDirectoryEntry cdimages)
    {
        var pedName = GetPedName(sex);
        var projectName = GetProjectName(counter);
        var scope = GetScope(sex);

        var drawables = _addon.Drawables.Where(x => x.Sex == sex).ToList();
        var drawableGroups = drawables.Select((x, i) => new { Index = i, Value = x })
                                       .GroupBy(x => x.Value.Number / GlobalConstants.MAX_DRAWABLES_IN_ADDON)
                                       .Select(x => x.Select(v => v.Value).ToList())
                                       .ToList();

        var yddPathsDict = await BatchResaveYdd(drawables, scope);

        var genderRpfName = sex == SexType.male ? "_male" : "_female";
        var componentsRpf = RpfFile.CreateNew(cdimages, projectName + genderRpfName + ".rpf");
        var componentsFolder = RpfFile.CreateDirectory(componentsRpf.Root, pedName + "_" + projectName);
        RpfFile propsRpf = null;
        RpfDirectoryEntry propsFolder = null;
        if (_addon.HasProps())
        {
            propsRpf = RpfFile.CreateNew(cdimages, projectName + genderRpfName + "_p" + ".rpf");
            propsFolder = RpfFile.CreateDirectory(propsRpf.Root, pedName + "_p_" + projectName);
        }

        RpfFile.CreateFile(componentsRpf.Root, $"{pedName}_{projectName}.ymt", ymtBytes);

        // RpfFile is not thread-safe: textures are produced in parallel but written into the archive
        // one at a time, in the original order. The window bounds how many finished files wait in memory.
        var pending = new Queue<PendingRpfEntry>();
        int window = _maxParallelism * 2;

        async Task FlushAsync(int keep)
        {
            while (pending.Count > keep)
            {
                var entry = pending.Dequeue();
                var bytes = await entry.Bytes;
                if (bytes == null)
                {
                    _reporter.Warning($"[{scope}] Skipping corrupted texture: {entry.DisplayName}");
                    _reporter.Complete(entry.Weight);
                    continue;
                }

                RpfFile.CreateFile(entry.Folder, entry.FileName, bytes);
                _reporter.Complete(entry.Weight, entry.Weight > 0 ? $"[{scope}] {entry.Description} {entry.FileName}" : null);
            }
        }

        foreach (var group in drawableGroups)
        {
            foreach (var d in group)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var tempYddPath = yddPathsDict[d];
                RpfDirectoryEntry folder = d.IsProp ? propsFolder : componentsFolder;

                // Drawable progress was already reported by BatchResaveYdd, so it carries no weight here.
                pending.Enqueue(new PendingRpfEntry(folder, $"{d.Name}{Path.GetExtension(d.FullFilePath)}", d.Name, "drawable",
                    Task.Run(() => File.ReadAllBytes(tempYddPath), _cancellationToken), 0));
                await FlushAsync(window);

                foreach (var t in d.Textures)
                {
                    var displayName = RemoveInvalidChars(t.GetBuildName());
                    var fileName = $"{displayName}{Path.GetExtension(t.FullFilePath)}";
                    var weight = GetTextureWeight(t, BuildResourceType.Singleplayer);

                    var bytes = t.IsOptimizedDuringBuild
                        ? ProduceTextureBytesAsync(t)
                        : Task.Run(() => File.ReadAllBytes(t.FullFilePath), _cancellationToken);

                    pending.Enqueue(new PendingRpfEntry(folder, fileName, t.DisplayName,
                        t.IsOptimizedDuringBuild ? $"texture optimized → {DescribeOptimization(t)}:" : "texture", bytes, weight));
                    await FlushAsync(window);
                }
            }
        }

        await FlushAsync(0);
    }

    private sealed record PendingRpfEntry(RpfDirectoryEntry Folder, string FileName, string DisplayName, string Description, Task<byte[]> Bytes, long Weight);


    #endregion


    #region Generic

    /// <summary>
    /// Produces the final bytes of a texture that needs ImageMagick (optimization or jpg/png/dds → ytd).
    /// Runs on the shared CPU limiter and memory budget. Returns null when the source image is corrupted.
    /// </summary>
    private async Task<byte[]> ProduceTextureBytesAsync(GTexture t)
    {
        await _cpuLimiter.WaitAsync(_cancellationToken);
        try
        {
            using var memory = await _memoryBudget.AcquireAsync(EstimateTextureCost(t), _cancellationToken);
            return await Task.Run(() => ImgHelper.Optimize(t, shouldSkipOptimization: !t.IsOptimizedDuringBuild), _cancellationToken);
        }
        finally
        {
            _cpuLimiter.Release();
        }
    }

    /// <summary>
    /// Writes one texture into the build output. Textures are independent of each other, so callers
    /// start all of them and await the batch; the limiters bound the actual concurrency.
    /// </summary>
    private async Task WriteTextureFileAsync(GTexture t, string finalPath, string scope, bool convertNonYtd, bool overwrite)
    {
        var weight = GetTextureWeight(t, convertNonYtd ? BuildResourceType.FiveM : BuildResourceType.AltV);
        var fileName = Path.GetFileName(finalPath);
        fileName = fileName[(fileName.LastIndexOf('^') + 1)..];

        try
        {
            if (t.IsOptimizedDuringBuild || (convertNonYtd && t.Extension != ".ytd"))
            {
                var bytes = await ProduceTextureBytesAsync(t);
                if (bytes == null)
                {
                    _reporter.Warning($"[{scope}] Skipping corrupted texture: {t.DisplayName}");
                    _reporter.Complete(weight);
                    return;
                }

                await File.WriteAllBytesAsync(finalPath, bytes);
                _reporter.Complete(weight, t.IsOptimizedDuringBuild
                    ? $"[{scope}] texture {fileName} optimized → {DescribeOptimization(t)}"
                    : $"[{scope}] texture {fileName} converted from {t.Extension}");
                return;
            }

            await _ioLimiter.WaitAsync(_cancellationToken);
            try
            {
                await Task.Run(() => File.Copy(t.FullFilePath, finalPath, overwrite), _cancellationToken);
            }
            finally
            {
                _ioLimiter.Release();
            }

            _reporter.Complete(weight, $"[{scope}] texture {fileName} copied");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _reporter.Error($"[{scope}] Texture {t.DisplayName} failed: {ex.Message}");
            throw;
        }
    }

    private static string DescribeOptimization(GTexture t)
    {
        var d = t.OptimizeDetails;
        return d == null ? "default settings" : $"{d.Width}x{d.Height} {d.Compression?.Replace("D3DFMT_", "")} ({d.MipMapCount} mips)";
    }

    private async Task CopyExtraFileAsync(string source, string destination)
    {
        await _ioLimiter.WaitAsync(_cancellationToken);
        try
        {
            await FileHelper.CopyAsync(source, destination);
        }
        finally
        {
            _ioLimiter.Release();
        }
    }

    private void AddFirstPersonFile(string name)
    {
        lock (firstPersonFiles)
        {
            firstPersonFiles.Add(name);
        }
    }

    private void AddBuildTasksForSex(Addon selectedAddon, SexType sexType, List<Task> tasks, List<string> metaFiles, int counter, RpfDirectoryEntry cdimages = null, RpfDirectoryEntry dataFolder = null)
    {
        if (selectedAddon.HasSex(sexType))
        {
            var bytes = BuildYMT(sexType);
            if (_buildResourceType == BuildResourceType.FiveM)
            {
                tasks.Add(BuildFiveMFilesAsync(sexType, bytes, counter));
            }
            else if (_buildResourceType == BuildResourceType.AltV)
            {
                tasks.Add(BuildAltVFilesAsync(sexType, bytes, counter));
            }
            else if (_buildResourceType == BuildResourceType.Singleplayer)
            {
                var (name, metaBytes) = BuildMeta(sexType);
                RpfFile.CreateFile(dataFolder, name, metaBytes);
                tasks.Add(BuildSingleplayerFilesAsync(sexType, bytes, counter, cdimages));
            }

            if (_buildResourceType != BuildResourceType.Singleplayer)
            {
                var (metaName, metaContent) = BuildMeta(sexType);
                metaFiles.Add(metaName);

                var path = _buildResourceType == BuildResourceType.FiveM ? Path.Combine(_buildPath, metaName) : Path.Combine(_buildPath, "stream", metaName);
                tasks.Add(File.WriteAllBytesAsync(path, metaContent));
            }
        }
    }

    private byte[] BuildYMT(SexType sex)
    {
        var mb = new MetaBuilder();
        var mdb = mb.EnsureBlock(MetaName.CPedVariationInfo);
        var CPed = new CPedVariationInfo
        {
            bHasDrawblVariations = 1,
            bHasTexVariations = 1,
            bHasLowLODs = 0,
            bIsSuperLOD = 0
        };

        ArrayOfBytes12 availComp = new();
        var generatedAvailComp = _addon.GenerateAvailComp();
        availComp.SetBytes(generatedAvailComp);
        CPed.availComp = availComp;

        var allDrawables = _addon.Drawables.Where(x => x.Sex == sex).ToList();
        var allCompDrawablesArray = allDrawables.Where(x => !x.IsProp).ToArray();
        var allPropDrawablesArray = allDrawables.Where(x => x.IsProp).ToArray();

        var components = new Dictionary<byte, CPVComponentData>();
        for (byte i = 0; i < generatedAvailComp.Length; i++)
        {
            if (generatedAvailComp[i] == 255) { continue; }

            var drawablesArray = allCompDrawablesArray.Where(x => x.TypeNumeric == i).ToArray();
            var drawables = new CPVDrawblData[drawablesArray.Length];

            for (int d = 0; d < drawables.Length; d++)
            {
                drawables[d].propMask = (byte)(drawablesArray[d].HasSkin ? 17 : 1);
                drawables[d].numAlternatives = (byte)(string.IsNullOrEmpty(drawablesArray[d].FirstPersonPath) ? 0 : 1);
                drawables[d].clothData = new CPVDrawblData__CPVClothComponentData() { ownsCloth = (byte)(string.IsNullOrEmpty(drawablesArray[d].ClothPhysicsPath) ? 0 : 1) };

                var texturesArray = drawablesArray[d].Textures.ToArray();
                var textures = new CPVTextureData[texturesArray.Length];
                for (int t = 0; t < textures.Length; t++)
                {
                    textures[t].texId = (byte)(drawablesArray[d].HasSkin ? 1 : 0);
                    textures[t].distribution = 255; //seems to be always 255
                }
                drawables[d].aTexData = mb.AddItemArrayPtr(MetaName.CPVTextureData, textures);
            };

            components[i] = new CPVComponentData()
            {
                numAvailTex = (byte)drawablesArray.Sum(y => y.Textures.Count),
                aDrawblData3 = mb.AddItemArrayPtr(MetaName.CPVDrawblData, drawables)
            };
        }

        CPed.aComponentData3 = mb.AddItemArrayPtr(MetaName.CPVComponentData, components.Values.ToArray());

        var compInfos = new CComponentInfo[allCompDrawablesArray.Length];
        for (int i = 0; i < compInfos.Length; i++)
        {
            var drawable = allCompDrawablesArray[i];
            compInfos[i].pedXml_audioID = JenkHash.GenHash(drawable.Audio);
            compInfos[i].pedXml_audioID2 = JenkHash.GenHash("none"); //todo
            compInfos[i].pedXml_expressionMods = new ArrayOfFloats5 { f0 = 0, f1 = 0, f2 = 0, f3 = 0, f4 = drawable.EnableHighHeels ? drawable.HighHeelsValue : 0 }; //expression mods
            compInfos[i].flags = (uint)drawable.Flags;
            compInfos[i].inclusions = 0;
            compInfos[i].exclusions = 0;
            compInfos[i].pedXml_vfxComps = ePedVarComp.PV_COMP_HEAD;
            compInfos[i].pedXml_flags = 0;
            compInfos[i].pedXml_compIdx = (byte)drawable.TypeNumeric;
            compInfos[i].pedXml_drawblIdx = (byte)drawable.Number;
        }

        CPed.compInfos = mb.AddItemArrayPtr(MetaName.CComponentInfo, compInfos);

        var propInfo = new CPedPropInfo
        {
            numAvailProps = (byte)allPropDrawablesArray.Length
        };

        var props = new CPedPropMetaData[allPropDrawablesArray.Length];
        for (int i = 0; i < props.Length; i++)
        {
            var prop = allPropDrawablesArray[i];
            props[i].audioId = JenkHash.GenHash(prop.Audio);
            props[i].expressionMods = new ArrayOfFloats5 { f0 = prop.EnableHairScale ? -prop.HairScaleValue : 0, f1 = 0, f2 = 0, f3 = 0, f4 = 0 };

            var texturesArray = prop.Textures.ToArray();
            var textures = new CPedPropTexData[texturesArray.Length];
            for (int t = 0; t < textures.Length; t++)
            {
                textures[t].inclusions = 0;
                textures[t].exclusions = 0;
                textures[t].texId = (byte)t;
                textures[t].inclusionId = 0;
                textures[t].exclusionId = 0;
                textures[t].distribution = 255;
            }

            ePropRenderFlags renderFlag = 0;
            if (Enum.TryParse(prop.RenderFlag, out ePropRenderFlags res))
            {
                renderFlag = res;
            }

            props[i].texData = mb.AddItemArrayPtr(MetaName.CPedPropTexData, textures);
            props[i].renderFlags = renderFlag;
            props[i].propFlags = (uint)prop.Flags;
            props[i].flags = 0;
            props[i].anchorId = (byte)prop.TypeNumeric;
            props[i].propId = (byte)prop.Number;
            props[i].Unk_2894625425 = 0;
        }
        propInfo.aPropMetaData = mb.AddItemArrayPtr(MetaName.CPedPropMetaData, props);

        var uniqueProps = allPropDrawablesArray.GroupBy(x => x.TypeNumeric).Select(g => g.First()).ToArray();
        var anchors = new CAnchorProps[uniqueProps.Length];
        for (int i = 0; i < anchors.Length; i++)
        {
            var propsOfType = allPropDrawablesArray.Where(x => x.TypeNumeric == uniqueProps[i].TypeNumeric);
            List<byte> items = propsOfType.Select(p => (byte)p.Textures.Count).ToList();

            anchors[i].props = mb.AddByteArrayPtr([.. items]);
            anchors[i].anchor = ((eAnchorPoints)propsOfType.First().TypeNumeric);
        }
        propInfo.aAnchors = mb.AddItemArrayPtr(MetaName.CAnchorProps, anchors);

        CPed.propInfo = propInfo;
        var projectName = GetProjectName();
        CPed.dlcName = JenkHash.GenHash(projectName);

        mb.AddItem(MetaName.CPedVariationInfo, CPed);

        mb.AddStructureInfo(MetaName.CPedVariationInfo);
        mb.AddStructureInfo(MetaName.CPedPropInfo);
        mb.AddStructureInfo(MetaName.CPedPropTexData);
        mb.AddStructureInfo(MetaName.CAnchorProps);
        mb.AddStructureInfo(MetaName.CComponentInfo);
        mb.AddStructureInfo(MetaName.CPVComponentData);
        mb.AddStructureInfo(MetaName.CPVDrawblData);
        mb.AddStructureInfo(MetaName.CPVDrawblData__CPVClothComponentData);
        mb.AddStructureInfo(MetaName.CPVTextureData);
        mb.AddStructureInfo(MetaName.CPedPropMetaData);
        mb.AddEnumInfo(MetaName.ePedVarComp);
        mb.AddEnumInfo(MetaName.eAnchorPoints);
        mb.AddEnumInfo(MetaName.ePropRenderFlags);

        Meta meta = mb.GetMeta();
        meta.Name = projectName;

        byte[] data = ResourceBuilder.Build(meta, 2);

        return data;
    }

    private (string, byte[]) BuildMeta(SexType sex)
    {
        var eCharacter = sex == SexType.male ? "SCR_CHAR_MULTIPLAYER" : "SCR_CHAR_MULTIPLAYER_F";
        var genderLetter = GetGenderLetter(sex);
        var pedName = GetPedName(sex);
        var projectName = GetProjectName();

        StringBuilder sb = new();
        sb.AppendLine(MetaXmlBase.XmlHeader);

        MetaXmlBase.OpenTag(sb, 0, "ShopPedApparel");
        MetaXmlBase.StringTag(sb, 4, "pedName", pedName);
        MetaXmlBase.StringTag(sb, 4, "dlcName", projectName);
        MetaXmlBase.StringTag(sb, 4, "fullDlcName", pedName + "_" + projectName);
        MetaXmlBase.StringTag(sb, 4, "eCharacter", eCharacter);
        MetaXmlBase.StringTag(sb, 4, "creatureMetaData", "mp_creaturemetadata_" + genderLetter + "_" + projectName);

        MetaXmlBase.OpenTag(sb, 4, "pedOutfits");
        MetaXmlBase.CloseTag(sb, 4, "pedOutfits");
        MetaXmlBase.OpenTag(sb, 4, "pedComponents");
        MetaXmlBase.CloseTag(sb, 4, "pedComponents");
        MetaXmlBase.OpenTag(sb, 4, "pedProps");
        MetaXmlBase.CloseTag(sb, 4, "pedProps");

        MetaXmlBase.CloseTag(sb, 0, "ShopPedApparel");

        var xml = sb.ToString();


        var name = pedName + "_" + projectName + ".meta";

        var bytes = Encoding.UTF8.GetBytes(xml);
        return (name, bytes);
    }

    private void BuildFirstPersonAlternatesMeta()
    {
        if (firstPersonFiles.Count > 0)
        {

            StringBuilder sb = new();
            sb.AppendLine(MetaXmlBase.XmlHeader);

            MetaXmlBase.OpenTag(sb, 0, "FirstPersonAlternateData");
            MetaXmlBase.OpenTag(sb, 4, "alternates");

            // Sex/addon tasks add entries concurrently, so sort for a deterministic meta file.
            foreach (var file in firstPersonFiles.OrderBy(f => f, StringComparer.Ordinal))
            {
                MetaXmlBase.OpenTag(sb, 8, "Item");
                MetaXmlBase.StringTag(sb, 12, "assetName", file);
                MetaXmlBase.ValueTag(sb, 12, "alternate", "1");
                MetaXmlBase.CloseTag(sb, 8, "Item");
            }

            MetaXmlBase.CloseTag(sb, 4, "alternates");
            MetaXmlBase.CloseTag(sb, 0, "FirstPersonAlternateData");

            var finalPath = Path.Combine(_buildPath, $"first_person_alternates_{_projectName}.meta");
            File.WriteAllText(finalPath, sb.ToString());
        }
    }

    private void BuildPedAlternativeVariationsMeta()
    {
        var projectName = GetProjectName();
        var pedAlternativeVariations = new PedAlternativeVariations();

        foreach (var sex in new[] { SexType.male, SexType.female })
        {

            var addonMasksHideHair = _addon.Drawables
                .Where(x => x.Sex == sex && x.TypeNumeric == 1 && !x.IsProp && x.HidesHair)
                .ToList();

            var addonHairs = _addon.Drawables
                .Where(x => x.Sex == sex && x.TypeNumeric == 2 && !x.IsProp)
                .ToList();

            if (addonMasksHideHair.Count == 0 && addonHairs.Count == 0)
            {
                continue;
            }

            var pedName = sex == SexType.male ? "mp_m_freemode_01" : "mp_f_freemode_01";
            var ped = new PedVariation { Name = pedName };

            if (addonMasksHideHair.Count != 0)
            {
                var hairEntries = PedAlternativeVariationsHelper.GetHairEntriesForSex(sex);

                foreach (var hairEntry in hairEntries)
                {
                    foreach (var hairIndex in hairEntry.Indices)
                    {
                        ped.Switches.Add(new AlternateSwitch
                        {
                            DlcNameHash = string.IsNullOrEmpty(hairEntry.DlcNameHash) ? null : hairEntry.DlcNameHash,
                            Component = 2,
                            Index = hairIndex,
                            Alt = 1,
                            SourceAssets = [.. addonMasksHideHair.Select(mask => new SourceAsset
                            {
                                DlcNameHash = projectName,
                                Component = 1,
                                Index = mask.Number
                            })]
                        });
                    }
                }

                foreach (var hair in addonHairs)
                {
                    ped.Switches.Add(new AlternateSwitch
                    {
                        DlcNameHash = projectName,
                        Component = 2,
                        Index = hair.Number,
                        Alt = 1,
                        SourceAssets = [.. addonMasksHideHair.Select(mask => new SourceAsset
                        {
                            DlcNameHash = projectName,
                            Component = 1,
                            Index = mask.Number
                        })]
                    });
                }
            }

            if (addonHairs.Count != 0)
            {
                var maskEntries = PedAlternativeVariationsHelper.GetMaskEntriesForSex(sex);

                foreach (var hair in addonHairs)
                {
                    var sourceAssets = new List<SourceAsset>();
                    
                    foreach (var maskEntry in maskEntries)
                    {
                        sourceAssets.AddRange(maskEntry.Indices.Select(maskIndex => new SourceAsset
                        {
                            DlcNameHash = maskEntry.DlcNameHash,
                            Component = 1,
                            Index = maskIndex
                        }));
                    }

                    ped.Switches.Add(new AlternateSwitch
                    {
                        DlcNameHash = projectName,
                        Component = 2,
                        Index = hair.Number,
                        Alt = 1,
                        SourceAssets = sourceAssets
                    });
                }
            }

            pedAlternativeVariations.Peds.Add(ped);
        }

        if (pedAlternativeVariations.Peds.Count == 0)
        {
            return;
        }

        var xmlDoc = pedAlternativeVariations.ToXml();
        var finalPath = Path.Combine(_buildPath, $"pedalternativevariations_{projectName}.meta");
        xmlDoc.Save(finalPath);
    }

    private static RbfFile GenerateCreatureMetadata(List<GDrawable> drawables)
    {
        //taken from ymteditor because it works fine xd
        var shouldGenCreatureHeels = drawables.Any(x => x.EnableHighHeels);
        var shouldGenCreatureHats = drawables.Any(x => x.EnableHairScale);
        if (!shouldGenCreatureHeels && !shouldGenCreatureHats) return null;

        XElement xml = new("CCreatureMetaData");
        XElement pedCompExpressions = new("pedCompExpressions");
        if (shouldGenCreatureHeels)
        {
            var feetDrawables = drawables.Where(x => x.TypeNumeric == 6 && x.IsComponent);
            foreach (var comp in feetDrawables)
            {
                XElement pedCompItem = new("Item");
                pedCompItem.Add(new XElement("pedCompID", new XAttribute("value", string.Format("0x{0:X}", 6))));
                pedCompItem.Add(new XElement("pedCompVarIndex", new XAttribute("value", string.Format("0x{0:X}", comp.Number))));
                pedCompItem.Add(new XElement("pedCompExpressionIndex", new XAttribute("value", string.Format("0x{0:X}", 4))));
                pedCompItem.Add(new XElement("tracks", new XAttribute("content", "char_array"), 33));
                pedCompItem.Add(new XElement("ids", new XAttribute("content", "short_array"), 28462));
                pedCompItem.Add(new XElement("types", new XAttribute("content", "char_array"), 2));
                pedCompItem.Add(new XElement("components", new XAttribute("content", "char_array"), 1));
                pedCompExpressions.Add(pedCompItem);
            }
        }
        xml.Add(pedCompExpressions);

        XElement pedPropExpressions = new("pedPropExpressions");
        if (shouldGenCreatureHats)
        {
            //all original GTA have that one first entry, without it, fivem was sometimes crashing(?)
            XElement FirstpedPropItem = new("Item");
            FirstpedPropItem.Add(new XElement("pedPropID", new XAttribute("value", string.Format("0x{0:X}", 0))));
            FirstpedPropItem.Add(new XElement("pedPropVarIndex", new XAttribute("value", string.Format("0x{0:X}", -1))));
            FirstpedPropItem.Add(new XElement("pedPropExpressionIndex", new XAttribute("value", string.Format("0x{0:X}", -1))));
            FirstpedPropItem.Add(new XElement("tracks", new XAttribute("content", "char_array"), 33));
            FirstpedPropItem.Add(new XElement("ids", new XAttribute("content", "short_array"), 13201));
            FirstpedPropItem.Add(new XElement("types", new XAttribute("content", "char_array"), 2));
            FirstpedPropItem.Add(new XElement("components", new XAttribute("content", "char_array"), 1));
            pedPropExpressions.Add(FirstpedPropItem);

            foreach (var prop in drawables.Where(x => x.TypeNumeric == 0 && x.IsProp))
            {
                XElement pedPropItem = new("Item");
                pedPropItem.Add(new XElement("pedPropID", new XAttribute("value", string.Format("0x{0:X}", 0))));
                pedPropItem.Add(new XElement("pedPropVarIndex", new XAttribute("value", string.Format("0x{0:X}", prop.Number))));
                pedPropItem.Add(new XElement("pedPropExpressionIndex", new XAttribute("value", string.Format("0x{0:X}", 0))));
                pedPropItem.Add(new XElement("tracks", new XAttribute("content", "char_array"), 33));
                pedPropItem.Add(new XElement("ids", new XAttribute("content", "short_array"), 13201));
                pedPropItem.Add(new XElement("types", new XAttribute("content", "char_array"), 2));
                pedPropItem.Add(new XElement("components", new XAttribute("content", "char_array"), 1));
                pedPropExpressions.Add(pedPropItem);
            }
        }
        xml.Add(pedPropExpressions);

        //create XmlDocument from XElement
        var xmldoc = new XmlDocument();
        xmldoc.Load(xml.CreateReader());

        RbfFile rbf = XmlRbf.GetRbf(xmldoc);
        return rbf;
    }

    /// <summary>
    /// Batch process multiple drawables in parallel to improve performance
    /// </summary>
    private async Task<Dictionary<GDrawable, string>> BatchResaveYdd(IEnumerable<GDrawable> drawables, string scope)
    {
        var result = new Dictionary<GDrawable, string>();
        var tasks = new List<Task>();

        foreach (var drawable in drawables)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!NeedsYddResave(drawable))
            {
                // Nothing to rebuild: the original file is copied later, no CPU slot needed.
                result[drawable] = drawable.FullFilePath;
                _reporter.Complete(BuildReporter.WeightCopy, $"[{scope}] drawable {drawable.Name}");
                continue;
            }

            tasks.Add(Task.Run(async () =>
            {
                await _cpuLimiter.WaitAsync(_cancellationToken);
                try
                {
                    using var memory = await _memoryBudget.AcquireAsync(EstimateYddCost(drawable), _cancellationToken);
                    var path = await ResaveYdd(drawable);
                    lock (result)
                    {
                        result[drawable] = path;
                    }
                    _reporter.Complete(BuildReporter.WeightYddResave, $"[{scope}] drawable {drawable.Name} rebuilt (embedded textures)");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _reporter.Error($"[{scope}] Drawable {drawable.Name} failed: {ex.Message}");
                    throw;
                }
                finally
                {
                    _cpuLimiter.Release();
                }
            }, _cancellationToken));
        }

        await Task.WhenAll(tasks);
        return result;
    }

    public async Task<string> ResaveYdd(GDrawable dr)
    {
        try
        {
            string inputPath = dr.FullFilePath;
            string uniqueFileName = $"{dr.Id}_{Path.GetFileName(inputPath)}";
            string outputPath = Path.Combine(_buildTempFolderPath, uniqueFileName);

            // Encrypted drawables, drawables without embedded textures or without embedded changes are copied as-is
            if (!NeedsYddResave(dr))
            {
                return inputPath;
            }

            var texturesToProcess = dr.Details.EmbeddedTextures.Where(kvp =>
            {
                var embeddedDto = kvp.Value;
                return embeddedDto.IsOptimizedDuringBuild || embeddedDto.HasReplacement || embeddedDto.OriginalName != embeddedDto.Details.Name;
            });

            var fileBytes = await FileHelper.ReadAllBytesAsync(inputPath);
            var yddFile = new YddFile();
            await yddFile.LoadAsync(fileBytes);

            var drawable = yddFile.Drawables.FirstOrDefault()
                          ?? throw new InvalidOperationException($"No drawables found in YDD: {inputPath}");

            if (drawable.ShaderGroup.TextureDictionary == null)
            {
                var dict = new TextureDictionary();
                drawable.ShaderGroup.TextureDictionary = dict;

                drawable.ShaderGroup.TextureDictionaryPointer = 1;

                drawable.ShaderGroup.TextureDictionary.Dict = new Dictionary<uint, Texture>();
                drawable.ShaderGroup.TextureDictionary.Textures = new ResourcePointerList64<Texture> { data_items = [] };
                drawable.ShaderGroup.TextureDictionary.TextureNameHashes = new ResourceSimpleList64_uint { data_items = [] };
            }

            var texturesToAdd = new List<Texture>();
            var textureHashesToAdd = new List<uint>();

            foreach (var kvp in texturesToProcess)
            {
                var embeddedDto = kvp.Value;

                // Rebuild the replacement texture from its persisted file if needed (after a
                // save/load only the path survives). Use this local instead of HasReplacement:
                // HasReplacement is true as soon as a path is set, but the data may fail to load.
                bool hasReplacement = embeddedDto.EnsureReplacementLoaded() && embeddedDto.ReplacementTextureData != null;

                var originalTexturePair = drawable.ShaderGroup.TextureDictionary.Dict
                    .FirstOrDefault(x => x.Value?.Name == embeddedDto.OriginalName);

                // If original texture doesn't exist but we have a replacement, add it as new texture
                if (originalTexturePair.Value == null && hasReplacement)
                {
                    Texture newTexture;
                    if (embeddedDto.IsOptimizedDuringBuild)
                    {
                        var dds = DDSIO.GetDDSFile(embeddedDto.ReplacementTextureData);
                        var optimizedBytes = await ImgHelper.Optimize(dds, embeddedDto.OptimizeDetails);
                        if (optimizedBytes == null)
                        {
                            _reporter.Warning($"Skipping corrupted embedded texture: {embeddedDto.Details.Name} in drawable {dr.Name}");
                            continue;
                        }
                        newTexture = DDSIO.GetTexture(optimizedBytes);
                    }
                    else
                    {
                        newTexture = embeddedDto.ReplacementTextureData;
                    }

                    newTexture.Name = embeddedDto.Details.Name;
                    newTexture.NameHash = JenkHash.GenHash(newTexture.Name);

                    var texList = drawable.ShaderGroup.TextureDictionary.Textures.data_items?.ToList() ?? [];
                    texList.Add(newTexture);

                    drawable.ShaderGroup.TextureDictionary.BuildFromTextureList(texList);

                    bool isSpec = embeddedDto.Details.Type.Contains("specular", StringComparison.OrdinalIgnoreCase);
                    bool isBump = embeddedDto.Details.Type.Contains("normal", StringComparison.OrdinalIgnoreCase);

                    foreach (var shader in drawable.ShaderGroup.Shaders.data_items)
                    {
                        var plist = shader.ParametersList;
                        var parameters = plist.Parameters;
                        var hashes = plist.Hashes;

                        for (int i = 0; i < parameters.Length; i++)
                        {
                            string samplerName = hashes[i].ToString();

                            // normal map
                            if (isBump && samplerName.Equals("bumpsampler", StringComparison.OrdinalIgnoreCase))
                            {
                                parameters[i].DataType = 0;
                                parameters[i].Data = newTexture;
                                break;
                            }

                            // specular map
                            if (isSpec && samplerName.Equals("specsampler", StringComparison.OrdinalIgnoreCase))
                            {
                                parameters[i].DataType = 0;
                                parameters[i].Data = newTexture;
                                break;
                            }
                        }
                    }

                    continue;
                }

                if (originalTexturePair.Value == null)
                {
                    _reporter.Warning($"Original texture '{embeddedDto.OriginalName}' not found in TextureDictionary for drawable {dr.Name}. Skipping.");
                    continue;
                }

                Texture textureToUpdate;
                if (hasReplacement && embeddedDto.IsOptimizedDuringBuild)
                {
                    var dds = DDSIO.GetDDSFile(embeddedDto.ReplacementTextureData);
                    var optimizedBytes = await ImgHelper.Optimize(dds, embeddedDto.OptimizeDetails);
                    if (optimizedBytes == null)
                    {
                        _reporter.Warning($"Skipping corrupted embedded texture: {embeddedDto.Details.Name} in drawable {dr.Name}");
                        continue;
                    }
                    textureToUpdate = DDSIO.GetTexture(optimizedBytes);
                }
                else if (hasReplacement)
                {
                    textureToUpdate = embeddedDto.ReplacementTextureData;
                }
                else if (embeddedDto.IsOptimizedDuringBuild)
                {
                    var dds = DDSIO.GetDDSFile(originalTexturePair.Value);
                    var optimizedBytes = await ImgHelper.Optimize(dds, embeddedDto.OptimizeDetails);
                    if (optimizedBytes == null)
                    {
                        _reporter.Warning($"Skipping corrupted embedded texture: {embeddedDto.Details.Name} in drawable {dr.Name}");
                        continue;
                    }
                    textureToUpdate = DDSIO.GetTexture(optimizedBytes);
                }
                else
                {
                    textureToUpdate = originalTexturePair.Value;
                }

                if (textureToUpdate.Name != embeddedDto.Details.Name)
                {
                    textureToUpdate.Name = embeddedDto.Details.Name;
                    textureToUpdate.NameHash = JenkHash.GenHash(textureToUpdate.Name);
                }

                if (hasReplacement || embeddedDto.IsOptimizedDuringBuild)
                {
                    drawable.ShaderGroup.TextureDictionary.Dict[originalTexturePair.Key] = textureToUpdate;

                    var texturesList = drawable.ShaderGroup.TextureDictionary.Textures;
                    for (int i = 0; i < texturesList.Count; i++)
                    {
                        if (texturesList[i].Name == embeddedDto.OriginalName)
                        {
                            texturesList[i] = textureToUpdate;
                            break;
                        }
                    }
                }

                foreach (var shader in drawable.ShaderGroup.Shaders.data_items)
                {
                    var parameters = shader.ParametersList.Parameters;
                    var hashes = shader.ParametersList.Hashes;

                    for (int i = 0; i < hashes.Length && i < parameters.Length; i++)
                    {
                        if (parameters[i].Data is CodeWalker.GameFiles.Texture embeddedTex)
                        {
                            if (embeddedTex.Name == embeddedDto.OriginalName)
                            {
                                parameters[i].Data = textureToUpdate;
                            }
                        }
                        else if (parameters[i].Data is TextureBase tb)
                        {
                            var referencedTexture = drawable.ShaderGroup.TextureDictionary?.Lookup(tb.NameHash);
                            if (referencedTexture?.Name == embeddedDto.OriginalName)
                            {
                                tb.Name = textureToUpdate.Name;
                                tb.NameHash = JenkHash.GenHash(textureToUpdate.Name);
                            }
                        }
                    }
                }
            }

            byte[] outputBytes = yddFile.Save();
            var testload = new YddFile();

            await File.WriteAllBytesAsync(outputPath, outputBytes);

            return outputPath;
        }
        catch (Exception ex)
        {
            LogHelper.Log($"Exception occurred while processing file: {dr.Name}\nException: {ex}", Views.LogType.Error);
            throw;
        }
    }

    private static string RemoveInvalidChars(string input)
    {
        return string.Concat(input.Split(Path.GetInvalidFileNameChars()));
    }

    private static string GetGenderLetter(SexType sex)
    {
        return sex switch
        {
            SexType.male => "m",
            SexType.female => "f",
            _ => throw new ArgumentOutOfRangeException(nameof(sex), sex, "Wrong sexType GetGenderLetter")
        };
    }

    private static string GetPedName(SexType sex)
    {
        return sex switch
        {
            SexType.male => "mp_m_freemode_01",
            SexType.female => "mp_f_freemode_01",
            _ => throw new ArgumentOutOfRangeException(nameof(sex), sex, "Wrong sexType GetPedName")
        };
    }


    

    private void CleanupBuildOutputDirectory()
    {
        try
        {
            // only delete if the path explicitly ends with "build_output"
            if (!_baseBuildPath.EndsWith("build_output", StringComparison.OrdinalIgnoreCase))
            {
                _reporter.Warning($"Skipping cleanup: Build path does not end with 'build_output'. Path: {_baseBuildPath}");
                return;
            }

            // ensure the path is not a root directory
            if (Path.GetPathRoot(_baseBuildPath) == _baseBuildPath)
            {
                _reporter.Warning($"Skipping cleanup: Cannot delete root directory. Path: {_baseBuildPath}");
                return;
            }

            if (Directory.Exists(_baseBuildPath))
            {
                try
                {
                    Directory.Delete(_baseBuildPath, true);
                    _reporter.Info($"Deleted existing build output directory: {_baseBuildPath}");
                }
                catch (Exception ex)
                {
                    _reporter.Warning($"Failed to delete build output directory: {ex.Message}");
                }
            }

            Directory.CreateDirectory(_baseBuildPath);
        }
        catch (Exception ex)
        {
            _reporter.Warning($"Error during build output cleanup: {ex.Message}");
        }
    }
    /// <summary>
    /// Removes what a cancelled build leaves behind that is never valid on its own: the temp folder and,
    /// for singleplayer, the half-written dlc.rpf. Loose FiveM/AltV output stays for inspection.
    /// </summary>
    public void CleanupAfterCancel()
    {
        CleanupBuildTempDirectory();

        if (_buildResourceType == BuildResourceType.Singleplayer)
        {
            var dlcRpfPath = Path.Combine(_baseBuildPath, "dlc.rpf");
            try
            {
                if (File.Exists(dlcRpfPath))
                {
                    File.Delete(dlcRpfPath);
                    _reporter.Info($"Deleted incomplete {dlcRpfPath}");
                }
            }
            catch (Exception ex)
            {
                _reporter.Warning($"Could not delete incomplete dlc.rpf: {ex.Message}");
            }
        }
    }

    private void CleanupBuildTempDirectory()
    {
        try
        {
            if (Directory.Exists(_buildTempFolderPath))
            {
                Directory.Delete(_buildTempFolderPath, true);
                _reporter.Info($"Deleted build temp directory: {_buildTempFolderPath}");
            }
        }
        catch (Exception ex)
        {
            _reporter.Warning($"Failed to clean up temp directory: {ex.Message}");
        }
    }

    #endregion
}
