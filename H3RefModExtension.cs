using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace H3RefMod;

/// <summary>
/// Treats cached MiniMax H3 RefMods as SwarmUI embeddings. A prompt's normal
/// <c>&lt;embed:...&gt;</c> tag selects the RefMod in the embedding library; this
/// extension recognizes its safetensors metadata and emits the two third-party
/// ComfyUI nodes required to load and inject it into H3 conditioning.
/// </summary>
public class H3RefModExtension : Extension
{
    public const string RefModFeatureFlag = "minimax_h3_refmods";
    public const string RefModModelClassId = "minimax-h3/refmod";
    public const string SigmaShiftNodeName = "MiniMaxH3SigmaShift";
    public const string LoaderNodeName = "MiniMaxH3RefModsLoader";
    public const string ApplyNodeName = "MiniMaxH3RefModApply";
    public const string SamplerNodeName = "SwarmKSampler";
    public const int MaxRefMods = 8;
    public const double StepPriority = 1000;

    // This group exists solely as the stable UI target for SwarmUI's standard
    // install-feature button. RefMod choice remains in the embedding picker.
    public static T2IParamGroup InstallerGroup;
    public static T2IRegisteredParam<bool> InstallerPlaceholder;

    public override void OnInit()
    {
        Logs.Info("SwarmUI H3 RefMods Extension initializing...");
        RegisterRefModArchitecture();
        RegisterEmbeddingPromptHandler();
        // Model handlers refresh after backends report their available models.
        // Cached metadata can restore the former generic H3-embedding class on
        // that refresh, so reapply our header-based classification afterwards.
        Program.ModelRefreshEvent += ReclassifyCachedRefMods;

        // These mappings make the normal ComfyUI feature/install flow aware that
        // a RefMod-tagged generation needs ComfyUI-MiniMaxH3Mod.
        ComfyUIBackendExtension.NodeToFeatureMap[LoaderNodeName] = RefModFeatureFlag;
        ComfyUIBackendExtension.NodeToFeatureMap[ApplyNodeName] = RefModFeatureFlag;
        InstallableFeatures.RegisterInstallableFeature(new(
            "MiniMax H3 RefMods",
            RefModFeatureFlag,
            "https://github.com/Luisacaotica/ComfyUI-MiniMaxH3Mod",
            "Luisacaotica",
            "This will install ComfyUI-MiniMaxH3Mod, a third-party extension maintained by Luisacaotica. It loads and applies cached MiniMax H3 RefMods. Do you wish to install it?"
        ));
        RegisterInstallerGroup();
        ScriptFiles.Add("assets/h3_refmod_install.js");

        // ComfyUI-MiniMaxH3Mod normally registers models/refmods. Mirror the
        // same roots SwarmUI gives ComfyUI for embeddings under that node's
        // folder type, so the loader resolves embedding-library RefMods
        // directly without a duplicate directory or copied files.
        ComfyUISelfStartBackend.ModifyComfyYaml.Add(AddRefModEmbeddingPathsToComfyYaml);

        WorkflowGenerator.AddStep(ApplyRefMods, StepPriority);
    }

    /// <summary>
    /// Creates a parameter group for the install button, not for RefMod
    /// selection. The feature-gated placeholder is hidden by SwarmUI while the
    /// dependency is absent, but still causes this group to be rendered so the
    /// bundled JavaScript can add the standard installer button to it.
    /// </summary>
    private static void RegisterInstallerGroup()
    {
        InstallerGroup = new(
            Name: "RefMod",
            Toggles: false,
            Open: true,
            IsAdvanced: false,
            OrderPriority: 9,
            Description: "Install the ComfyUI-MiniMaxH3Mod node package required to use MiniMax H3 RefMods selected from the embedding library."
        );
        InstallerPlaceholder = T2IParamTypes.Register<bool>(new(
            Name: "H3 RefMod Nodes",
            Description: "Internal installer placeholder. RefMods are selected through the normal embedding picker, not this group.",
            Default: "false",
            IgnoreIf: "false",
            ID: "h3_refmod_installer_placeholder",
            Group: InstallerGroup,
            FeatureFlag: RefModFeatureFlag,
            IntentionalUnused: true
        ));
    }

    /// <summary>
    /// Adds a <c>refmods</c> alias for every configured SwarmUI embedding
    /// directory to the self-start ComfyUI model-path file. The third-party
    /// node registers <c>models/refmods</c> as a fallback, but it consults all
    /// registered <c>refmods</c> roots at load time; this alias makes the
    /// embedding library its primary root instead.
    /// </summary>
    private static string AddRefModEmbeddingPathsToComfyYaml(string yaml)
    {
        string[] roots = Program.ServerSettings.Paths.ModelRoot.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] embeddingFolders = (Program.ServerSettings.Paths.SDEmbeddingFolder + ";embeddings")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string additions = "\n";
        for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
        {
            string root = Utilities.CombinePathWithAbsolute(Environment.CurrentDirectory, roots[rootIndex]);
            additions += $"swarmui_refmod_embeddings{(rootIndex == 0 ? "" : (rootIndex + 1).ToString())}:\n";
            additions += $"    base_path: {root}\n";
            if (rootIndex == 0)
            {
                additions += "    is_default: true\n";
            }
            additions += "    refmods: |";
            HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
            foreach (string folder in embeddingFolders)
            {
                // Match SwarmUI's own generated YAML: only add each resolved
                // folder once, while preserving configured relative or absolute
                // paths for ComfyUI to resolve from the same base path.
                string resolved = Utilities.CombinePathWithAbsolute(root, folder);
                if (paths.Add(resolved))
                {
                    additions += $"\n       {folder}";
                }
            }
            additions += "\n\n";
        }
        return yaml + additions;
    }

    public override void OnPreLaunch()
    {
        // Model metadata may have been cached before this extension was added.
        // Reclassify those entries after SwarmUI has completed its model refresh.
        ReclassifyCachedRefMods();
    }

    public override void OnShutdown()
    {
        Program.ModelRefreshEvent -= ReclassifyCachedRefMods;
    }

    /// <summary>
    /// Reapply RefMod classification to the transient model list after every
    /// model refresh. This intentionally does not write SwarmUI's metadata
    /// database: the RefMod architecture is determined from the safetensors
    /// header, and keeping it transient avoids modifying or locking the user's
    /// existing metadata cache during backend startup.
    /// </summary>
    private static void ReclassifyCachedRefMods()
    {
        T2IModelClass refModClass = T2IModelClassSorter.ModelClasses[RefModModelClassId];
        int classified = 0;
        foreach (T2IModel model in Program.T2IModelSets["Embedding"].Models.Values)
        {
            if (IsRefModModel(model) && model.ModelClass != refModClass)
            {
                model.ModelClass = refModClass;
                classified++;
            }
        }
        if (classified > 0)
        {
            Logs.Info($"H3 RefMods: classified {classified} cached embedding-library file(s) as MiniMax H3 RefMod.");
        }
    }

    /// <summary>
    /// RefMods are not ordinary MiniMax H3 textual-inversion embeddings. Their
    /// upstream format identifies itself through the safetensors header metadata
    /// key <c>refmod_meta</c> (or legacy <c>audio_refmod_meta</c>), rather than the
    /// <c>qwen3vl_32b</c> tensor used by H3 text embeddings.
    /// </summary>
    private static void RegisterRefModArchitecture()
    {
        if (T2IModelClassSorter.ModelClasses.ContainsKey(RefModModelClassId))
        {
            return;
        }
        T2IModelClassSorter.Register(new()
        {
            ID = RefModModelClassId,
            CompatClass = T2IModelClassSorter.CompatMiniMaxH3,
            Name = "MiniMax H3 RefMod",
            StandardWidth = 960,
            StandardHeight = 960,
            IsThisModelOfClass = (_, header) => IsRefModHeader(header)
        });
    }

    private static bool IsRefModHeader(JObject header)
        => header?["__metadata__"]?["refmod_meta"] is not null
            || header?["__metadata__"]?["audio_refmod_meta"] is not null
            || header?["refmod_meta"] is not null
            || header?["audio_refmod_meta"] is not null;

    private static bool IsRefModModel(T2IModel model)
    {
        if (model is null)
        {
            return false;
        }
        if (model.ModelClass?.ID == RefModModelClassId)
        {
            return true;
        }
        return IsRefModHeader(T2IModel.GetMetadataHeaderFrom(model.RawFilePath));
    }

    /// <summary>
    /// The standard embedding picker always produces an embed tag. Intercept
    /// genuine RefMods while the tag is parsed, before backend selection or a
    /// text-encoder node can see it. Ordinary embeddings continue unchanged.
    /// </summary>
    private static void RegisterEmbeddingPromptHandler()
    {
        if (!T2IPromptHandling.PromptTagProcessors.TryGetValue("embed", out Func<string, T2IPromptHandling.PromptTagContext, string> standardEmbed))
        {
            Logs.Error("H3 RefMods: SwarmUI's embedding prompt handler was unavailable; RefMod prompt tags cannot be intercepted.");
            return;
        }
        string process(string data, T2IPromptHandling.PromptTagContext context)
        {
            string result = standardEmbed(data, context);
            const string prefix = "\0swarmembed:";
            const string suffix = "\0end";
            if (string.IsNullOrEmpty(result) || !result.StartsWith(prefix, StringComparison.Ordinal) || !result.EndsWith(suffix, StringComparison.Ordinal))
            {
                return result;
            }
            string name = result[prefix.Length..^suffix.Length];
            T2IModel model = Program.T2IModelSets["Embedding"].GetModel(name);
            if (!IsRefModModel(model))
            {
                return result;
            }
            // The stock handler records this as a normal text embedding. Remove
            // that requirement so backend selection does not look in ComfyUI's
            // ordinary embeddings folder for a RefMod.
            if (context.Input.ExtraMeta.TryGetValue("used_embeddings", out object used)
                && used is List<string> usedEmbeddings)
            {
                for (int i = usedEmbeddings.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(usedEmbeddings[i], name, StringComparison.OrdinalIgnoreCase))
                    {
                        usedEmbeddings.RemoveAt(i);
                        break;
                    }
                }
            }
            if (!context.Input.ExtraMeta.TryGetValue("h3_refmods", out object refModValue)
                || refModValue is not List<string> refMods)
            {
                refMods = [];
                context.Input.ExtraMeta["h3_refmods"] = refMods;
            }
            if (!refMods.Exists(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
            {
                refMods.Add(name);
            }
            return "";
        }
        T2IPromptHandling.PromptTagProcessors["embed"] = process;
        T2IPromptHandling.PromptTagProcessors["embedding"] = process;
    }

    private static void ApplyRefMods(WorkflowGenerator generator)
    {
        List<string> refMods = GetSelectedRefMods(generator);
        if (refMods.Count == 0)
        {
            return;
        }

        HashSet<string> shiftIds = [];
        generator.RunOnNodesOfClass(SigmaShiftNodeName, (id, _) => shiftIds.Add(id));
        if (shiftIds.Count == 0)
        {
            Logs.Warning("H3 RefMods: a RefMod embedding was selected, but the generation is not using MiniMax H3.");
            return;
        }

        // SwarmUI emits <embed:name> for all selections in the embedding library.
        // RefMods are conditioning payloads, not text-encoder embeddings, so remove
        // only their tags from text nodes before ComfyUI sees the workflow.
        RemoveRefModTagsFromPrompts(generator.Workflow, refMods);

        string loaderId = CreateLoaderNode(generator, refMods);
        int applied = 0;
        foreach (JProperty samplerProperty in generator.NodesOfClass(SamplerNodeName))
        {
            JObject samplerInputs = (samplerProperty.Value as JObject)?["inputs"] as JObject;
            if (samplerInputs?["model"] is not JArray model
                || samplerInputs["positive"] is not JArray conditioning
                || !ModelConnectionReachesH3(generator.Workflow, model, shiftIds))
            {
                continue;
            }
            string applyId = CreateApplyNode(generator, loaderId, conditioning);
            samplerInputs["positive"] = new JArray(applyId, 0);
            applied++;
        }

        if (applied == 0)
        {
            Logs.Warning("H3 RefMods: selected RefMods but found no MiniMax H3 sampler conditioning to modify.");
        }
        else
        {
            Logs.Debug($"H3 RefMods: applied {refMods.Count} RefMod embedding(s) to {applied} MiniMax H3 sampler branch(es).");
        }
    }

    /// <summary>Gets selected embedding-library entries whose detected architecture is RefMod.</summary>
    private static List<string> GetSelectedRefMods(WorkflowGenerator generator)
    {
        if (!generator.UserInput.ExtraMeta.TryGetValue("h3_refmods", out object used)
            || used is not List<string> embeddings)
        {
            return [];
        }
        List<string> result = [];
        foreach (string embedding in embeddings)
        {
            T2IModel model = Program.T2IModelSets["Embedding"].GetModel(embedding);
            if (!IsRefModModel(model))
            {
                continue;
            }
            string name = T2IParamTypes.CleanModelName(embedding);
            if (!result.Exists(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(name);
            }
        }
        return result;
    }

    private static string CreateLoaderNode(WorkflowGenerator generator, List<string> refMods)
    {
        JObject inputs = new() { ["show_info"] = false };
        for (int i = 0; i < MaxRefMods; i++)
        {
            int slot = i + 1;
            inputs[$"mod_{slot}"] = i < refMods.Count ? refMods[i] : "(none)";
            inputs[$"strength_{slot}"] = 1.0;
            inputs[$"copies_{slot}"] = 1;
            inputs[$"components_{slot}"] = "All";
            inputs[$"visual_strength_{slot}"] = 1.0;
            inputs[$"audio_strength_{slot}"] = 1.0;
        }
        return generator.CreateNode(LoaderNodeName, (_, node) => node["inputs"] = inputs);
    }

    private static string CreateApplyNode(WorkflowGenerator generator, string loaderId, JArray conditioning)
    {
        JObject inputs = new()
        {
            ["conditioning"] = conditioning,
            ["mods"] = new JArray(loaderId, 0),
            ["override"] = false,
            ["retention"] = 1.0,
            ["curve_direction"] = "constant",
            ["curve_shape"] = "linear",
            ["curve_value"] = 1.0,
            ["scramble_seed"] = -1,
            ["scramble_mode"] = "shuffle",
            ["scramble_keep"] = 1,
            ["max_total_tokens"] = 0
        };
        return generator.CreateNode(ApplyNodeName, (_, node) => node["inputs"] = inputs);
    }

    private static void RemoveRefModTagsFromPrompts(JObject workflow, List<string> refMods)
    {
        foreach (JProperty property in workflow.Properties())
        {
            if (property.Value is not JObject node || node["inputs"] is not JObject inputs)
            {
                continue;
            }
            RemoveRefModTags(inputs, refMods);
        }
    }

    private static void RemoveRefModTags(JToken token, List<string> refMods)
    {
        if (token is JObject obj)
        {
            foreach (JProperty property in obj.Properties())
            {
                if (property.Value is JValue value && value.Type == JTokenType.String)
                {
                    string text = value.Value<string>();
                    foreach (string refMod in refMods)
                    {
                        text = text.Replace($"<embed:{refMod}>", "", StringComparison.OrdinalIgnoreCase);
                    }
                    value.Value = text;
                }
                else
                {
                    RemoveRefModTags(property.Value, refMods);
                }
            }
        }
        else if (token is JArray array)
        {
            foreach (JToken item in array)
            {
                RemoveRefModTags(item, refMods);
            }
        }
    }

    /// <summary>Traces a sampler model input through model-patcher nodes to an H3 sigma-shift node.</summary>
    private static bool ModelConnectionReachesH3(JObject workflow, JArray connection, HashSet<string> shiftIds)
    {
        HashSet<string> visited = [];
        JArray current = connection;
        while (current.Count == 2)
        {
            string nodeId = $"{current[0]}";
            if (!visited.Add(nodeId))
            {
                return false;
            }
            if (shiftIds.Contains(nodeId))
            {
                return true;
            }
            if (workflow[nodeId] is not JObject node
                || node["inputs"] is not JObject inputs
                || inputs["model"] is not JArray upstream)
            {
                return false;
            }
            current = upstream;
        }
        return false;
    }
}
