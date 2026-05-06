using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EasyYoloOcr.Example.Wpf.Services;

/// <summary>
/// Provides intellisense-style completions for the WinAgent prompt input.
/// Handles command completion, parameter completion, and file system browsing.
/// Supports dynamic registration — call <see cref="AddCommand"/> or <see cref="AddFeature"/>
/// at any time and completions update automatically.
/// </summary>
public sealed class PromptIntellisense
{
    public enum BrowseTargetKind
    {
        None,
        File,
        Directory
    }

    /// <summary>A single completion item with display text, insert text, and description.</summary>
    public sealed class CompletionItem
    {
        public string DisplayText { get; set; } = "";
        public string InsertText { get; set; } = "";
        public string Description { get; set; } = "";
        public string Category { get; set; } = "";
        public bool IsDirectory { get; set; }
        /// <summary>Extended writeup shown in expanded tooltips / help panels.</summary>
        public string Writeup { get; set; } = "";
        /// <summary>Whether this item can be executed directly from intellisense.</summary>
        public bool IsExecutable { get; set; }
        public bool HasBrowseButton { get; set; }
        public BrowseTargetKind BrowseTarget { get; set; }
        public string BrowseTitle { get; set; } = "";
        public string BrowseFilter { get; set; } = "";
        public string BrowseInsertPrefix { get; set; } = "";
        public string BrowseInitialDirectory { get; set; } = "";

        public override string ToString() => DisplayText;
    }

    private sealed record PathCommandSpec(
        string Prefix,
        BrowseTargetKind Target,
        string Title,
        string Filter = "",
        bool FirstArgumentOnly = false);

    private static readonly PathCommandSpec[] PathCommandSpecs =
    [
        new("/fs search ", BrowseTargetKind.Directory, "Select directory to search", FirstArgumentOnly: true),
        new("/fs read ", BrowseTargetKind.File, "Select file to read"),
        new("/fs write ", BrowseTargetKind.File, "Select file to write", FirstArgumentOnly: true),
        new("/fs watch ", BrowseTargetKind.Directory, "Select directory to watch"),
        new("/fs unwatch ", BrowseTargetKind.Directory, "Select watched directory to remove"),
        new("/ai load ", BrowseTargetKind.File, "Select GGUF model", "GGUF models|*.gguf|All files|*.*"),
        new("/voice load ", BrowseTargetKind.File, "Select Piper ONNX voice model", "ONNX models|*.onnx|All files|*.*"),
        new("/caption ", BrowseTargetKind.File, "Select image to caption", "Image files|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|All files|*.*")
    ];

    /// <summary>All registered commands — dynamically extensible.</summary>
    private readonly List<CompletionItem> _commands = [];
    private readonly object _lock = new();

    public PromptIntellisense()
    {
        // Seed with all built-in commands and their writeups
        RegisterBuiltInCommands();
    }

    // ?? Dynamic registration API ??????????????????????????????????????????

    /// <summary>Register a single command into intellisense.</summary>
    public void AddCommand(CompletionItem item) { lock (_lock) _commands.Add(item); }

    /// <summary>Register multiple commands at once.</summary>
    public void AddCommands(IEnumerable<CompletionItem> items) { lock (_lock) _commands.AddRange(items); }

    /// <summary>Register an LLM / AI feature that appears in intellisense and is executable.</summary>
    public void AddFeature(string displayText, string insertText, string description, string writeup = "")
    {
        lock (_lock) _commands.Add(new CompletionItem
        {
            DisplayText = displayText,
            InsertText = insertText,
            Description = description,
            Writeup = writeup,
            Category = "AI Feature",
            IsExecutable = true
        });
    }

    /// <summary>Register agent slave actions so they appear in intellisense and can be invoked.</summary>
    public void AddAgentActions(IEnumerable<(string Syntax, string Description, bool ReturnsData)> actions)
    {
        lock (_lock)
        {
            foreach (var (syntax, desc, returns) in actions)
            {
                string suffix = returns ? " ? returns data" : "";
                _commands.Add(new CompletionItem
                {
                    DisplayText = $"? {syntax}",
                    InsertText = syntax,
                    Description = $"{desc}{suffix}",
                    Writeup = $"AI Agent action: {desc}. Syntax: {syntax}." + (returns ? " Returns data to the LLM for reasoning." : " Fire-and-forget action."),
                    Category = "Agent Action",
                    IsExecutable = true
                });
            }
        }
    }

    /// <summary>Remove all commands in a given category (useful before re-registering).</summary>
    public void ClearCategory(string category)
    {
        lock (_lock) _commands.RemoveAll(c => c.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Get a snapshot of all registered commands (for help display, etc.).</summary>
    public IReadOnlyList<CompletionItem> GetAllCommands()
    {
        lock (_lock) return [.. _commands];
    }

    private void RegisterBuiltInCommands()
    {
        _commands.AddRange([
            // ?? Search & Info ??
            new() { DisplayText = "/s <query>", InsertText = "/s ", Category = "Search",
                Description = "Search the web — returns raw JSON",
                Writeup = "Performs a web search and prints raw JSON results. Use this when you want the underlying result data instead of the rendered search cards." },
            new() { DisplayText = "/i <query>", InsertText = "/i ", Category = "Search",
                Description = "Quick info lookup — AI-powered answer from the web",
                Writeup = "Searches the web and uses the LLM (if loaded) to synthesize a concise answer. Ideal for factual questions, definitions, and quick lookups. Falls back to raw search results if no model is loaded." },
            new() { DisplayText = "/v <query>", InsertText = "/v ", Category = "Search",
                Description = "Search videos across the web",
                Writeup = "Searches for videos and displays results with thumbnails and playback links. Useful for tutorials, demos, and media content discovery." },
            new() { DisplayText = "/yt <query>", InsertText = "/yt ", Category = "Search",
                Description = "Search YouTube for videos",
                Writeup = "Searches YouTube specifically and renders results with thumbnails, duration, and channel info. Great for finding tutorials, music, and entertainment." },

            // ?? Recording & Playback ??
            new() { DisplayText = "/record <name>", InsertText = "/record ", Category = "Recording",
                Description = "Start recording mouse/keyboard events as a named sequence",
                Writeup = "Begins capturing all mouse clicks, key presses, and timing into a named sequence. Use '/stop' to end recording and save. Recorded sequences can be replayed with '/play' and become part of the training data." },
            new() { DisplayText = "/play <name>", InsertText = "/play ", Category = "Recording",
                Description = "Replay a saved sequence by name",
                Writeup = "Plays back a previously recorded sequence, reproducing all mouse and keyboard events with original timing. Use Tab to autocomplete sequence names." },
            new() { DisplayText = "/stop", InsertText = "/stop", Category = "Recording",
                Description = "Stop recording, playback, or watching",
                Writeup = "Stops the current active operation — whether it's a recording session, sequence playback, or screen observation watch. Safe to call at any time." },
            new() { DisplayText = "/recordings", InsertText = "/recordings", Category = "Recording",
                Description = "List all saved sequences and training data",
                Writeup = "Displays all recorded sequences with their names, event counts, and associated app names. Also shows training data statistics." },
            new() { DisplayText = "/delete <name>", InsertText = "/delete ", Category = "Recording",
                Description = "Delete a saved sequence by name",
                Writeup = "Permanently removes a recorded sequence from storage. Use '/recordings' first to see available names." },

            // ?? Utility ??
            new() { DisplayText = "/send <text>", InsertText = "/send ", Category = "Utility",
                Description = "Type text into the foreground window via simulated keystrokes",
                Writeup = "Sends the specified text as simulated keyboard input to whatever window is currently in the foreground. Useful for automating text entry in other applications." },
            new() { DisplayText = "/export", InsertText = "/export", Category = "Utility",
                Description = "Export .traineddata archive from collected training samples",
                Writeup = "Packages all collected OCR training data, corrections, and profiles into a .traineddata archive that can be shared or used to improve recognition accuracy." },
            new() { DisplayText = "/clear", InsertText = "/clear", Category = "Utility",
                Description = "Clear the console log and start a fresh conversation",
                Writeup = "Clears all console output and resets the conversation ID. Does not affect loaded models, recordings, or training data. Aliases: /cls" },
            new() { DisplayText = "/help", InsertText = "/help", Category = "Utility",
                Description = "Show the complete command reference with all categories",
                Writeup = "Displays a formatted help panel listing every available command organized by category (Search, Recording, AI, Multimodal, File System, etc.) with syntax and descriptions. Aliases: /?" },
            new() { DisplayText = "/sessions", InsertText = "/sessions", Category = "Utility",
                Description = "List past observation sessions with timestamps and stats",
                Writeup = "Shows all recorded observation sessions including start time, duration, number of screen captures, and associated application names." },
            new() { DisplayText = "/logs", InsertText = "/logs", Category = "Utility",
                Description = "Show prompt log history for the current session",
                Writeup = "Displays a scrollable history of all prompts entered in this session, along with timestamps. Useful for reviewing past interactions." },
            new() { DisplayText = "/commands", InsertText = "/commands", Category = "Utility",
                Description = "List all AI agent actions and capabilities",
                Writeup = "Shows every action the AI agent can perform including mouse control, keyboard input, screen reading, classification, database operations, search, and inference. Aliases: /features" },

            // ?? File System ??
            new() { DisplayText = "/fs search <path> [pattern]", InsertText = "/fs search ", Category = "File System",
                Description = "Search for files under a directory path with optional glob pattern",
                Writeup = "Recursively searches the specified directory for files matching the pattern (e.g., *.txt, *.log). Results include file paths, sizes, and modification dates." },
            new() { DisplayText = "/fs read <file>", InsertText = "/fs read ", Category = "File System",
                Description = "Read and display the contents of a file",
                Writeup = "Reads a file from disk and displays its contents in the console. Supports text files, JSON, XML, and other text-based formats. Large files are truncated with a size warning." },
            new() { DisplayText = "/fs write <file> <content>", InsertText = "/fs write ", Category = "File System",
                Description = "Write content to a file (creates or overwrites)",
                Writeup = "Writes the specified text content to a file. Creates the file if it doesn't exist, overwrites if it does. Use with caution — there is no undo." },
            new() { DisplayText = "/fs watch <path>", InsertText = "/fs watch ", Category = "File System",
                Description = "Watch a directory or file for changes in real-time",
                Writeup = "Sets up a file system watcher that reports created, modified, deleted, and renamed files in real-time. Useful for monitoring log files, build output, or data directories." },
            new() { DisplayText = "/fs unwatch [path]", InsertText = "/fs unwatch ", Category = "File System",
                Description = "Stop watching a path (omit path to stop all watches)",
                Writeup = "Removes a file system watcher. If no path is specified, all active watches are stopped." },
            new() { DisplayText = "/fs list", InsertText = "/fs list", Category = "File System",
                Description = "List all active file system watches",
                Writeup = "Shows every directory/file currently being monitored along with the number of events received from each." },

            // ?? AI / LLM ??
            new() { DisplayText = "/ai load <path>", InsertText = "/ai load ", Category = "AI",
                Description = "Load a GGUF model for local LLM inference",
                Writeup = "Loads a quantized GGUF model file using LLamaSharp for local AI inference. Supports Q4_K_M, Q5_K_M, Q8_0, and other quantization formats. The model is loaded into GPU/CPU memory and becomes available for chat, info lookups, and agent reasoning. Tab-complete to browse .gguf files." },
            new() { DisplayText = "/ai unload", InsertText = "/ai unload", Category = "AI",
                Description = "Unload the current LLM model and free memory",
                Writeup = "Disposes the loaded LLM model, freeing GPU/CPU memory. Any running inference is cancelled first." },
            new() { DisplayText = "/ai status", InsertText = "/ai status", Category = "AI",
                Description = "Show current model info (name, loaded state)",
                Writeup = "Displays the currently loaded model name and status. If no model is loaded, shows instructions for loading one." },
            new() { DisplayText = "/ai context", InsertText = "/ai context", Category = "AI",
                Description = "Refresh the LLM's training data context from collected samples",
                Writeup = "Rebuilds the system prompt using the latest training data, profiles, corrections, and session tags. Call this after collecting new training data or changing profiles to give the LLM updated context." },
            new() { DisplayText = "/ai stop", InsertText = "/ai stop", Category = "AI",
                Description = "Cancel the currently running LLM inference",
                Writeup = "Immediately cancels any in-progress LLM text generation. The partial output is preserved in the console." },
            new() { DisplayText = "/ai reset", InsertText = "/ai reset", Category = "AI",
                Description = "Reset conversation history and refresh context",
                Writeup = "Clears all conversation history (chat turns) from the LLM context and rebuilds the system prompt from training data. Useful when the conversation becomes stale or off-track." },
            new() { DisplayText = "/ai temp <value>", InsertText = "/ai temp ", Category = "AI",
                Description = "Set LLM creativity/temperature (0.0 = deterministic, 2.0 = max creative)",
                Writeup = "Controls the randomness of LLM output. 0.0 produces the most predictable text, 0.7 is a good default, 1.0+ introduces more variety and creativity. Values above 1.5 may produce incoherent output." },

            // ?? Multimodal (TTS / Generation / Captioning) ??
            new() { DisplayText = "/speak <text>", InsertText = "/speak ", Category = "Multimodal",
                Description = "Read text aloud using Piper neural text-to-speech",
                Writeup = "Converts the given text to speech using the Piper TTS engine with the currently selected voice model. Audio plays through the default output device. Use '/voice' to change voices." },
            new() { DisplayText = "/speak save <text>", InsertText = "/speak save ", Category = "Multimodal",
                Description = "Convert text to speech and save as a WAV file",
                Writeup = "Generates speech from text and saves it as a WAV audio file instead of playing it. The file path is displayed with a playback card." },
            new() { DisplayText = "/speak stop", InsertText = "/speak stop", Category = "Multimodal",
                Description = "Stop TTS audio playback immediately",
                Writeup = "Immediately stops any currently playing text-to-speech audio." },
            new() { DisplayText = "/voices", InsertText = "/voices", Category = "Multimodal",
                Description = "List available English Piper TTS voices",
                Writeup = "Shows all available English TTS voice models that can be used with '/voice <key>'. Voices vary in quality (low/medium/high) and speaker characteristics." },
            new() { DisplayText = "/voices all", InsertText = "/voices all", Category = "Multimodal",
                Description = "List all available TTS voices (all languages)",
                Writeup = "Shows every available TTS voice model across all supported languages including English, Spanish, French, German, and many more." },
            new() { DisplayText = "/voice <key>", InsertText = "/voice ", Category = "Multimodal",
                Description = "Set the active TTS voice (e.g., en_US-lessac-high)",
                Writeup = "Changes the text-to-speech voice to the specified model key. Use '/voices' to see available options. Higher quality models (high) sound more natural but may be slower." },
            new() { DisplayText = "/voice load <path>", InsertText = "/voice load ", Category = "Multimodal",
                Description = "Load a custom .onnx voice model from disk",
                Writeup = "Loads a custom Piper TTS voice model (.onnx format) from the specified file path. Use this for voices not included in the default collection." },
            new() { DisplayText = "/generate image <prompt>", InsertText = "/generate image ", Category = "Multimodal",
                Description = "Generate an image locally using ONNX Runtime diffusion",
                Writeup = "Generates an image from a text prompt using a local Stable Diffusion pipeline. Options: width, height, steps, cfg, seed. Example: /generate image A sunset over mountains steps=30 cfg=7.5" },
            new() { DisplayText = "/generate audio <prompt>", InsertText = "/generate audio ", Category = "Multimodal",
                Description = "Generate audio locally from a text description",
                Writeup = "Generates audio content from a text prompt. Options: duration, samplerate, steps. Example: /generate audio Ocean waves crashing on rocks duration=10" },
            new() { DisplayText = "/generate video <prompt>", InsertText = "/generate video ", Category = "Multimodal",
                Description = "Generate a video locally from a text description",
                Writeup = "Generates a video clip from a text prompt. Options: frames, fps, width, height, steps. Example: /generate video A cat walking on the moon frames=48 fps=24" },
            new() { DisplayText = "/generate va <prompt>", InsertText = "/generate va ", Category = "Multimodal",
                Description = "Generate video + audio combined from a text description",
                Writeup = "Generates both video and matching audio from a single text prompt, then combines them. Options: all video + audio options." },
            new() { DisplayText = "/caption <image-path>", InsertText = "/caption ", Category = "Multimodal",
                Description = "Generate a text caption/description for an image file",
                Writeup = "Analyzes the specified image file and generates a natural language description of its contents using an image captioning model." },

            // ?? Reflection ??
            new() { DisplayText = "/reflect", InsertText = "/reflect ", Category = "Reflection",
                Description = "Inspect and execute application internals via reflection",
                Writeup = "Access properties, fields, and methods on the running application instance. View values as JSON, call methods with parameters, and live-watch changing values. Supports dot-path navigation (e.g., _DataStore.SomeField) and parenthesis syntax for method calls." },
        new() { DisplayText = "/reflect assemblies", InsertText = "/reflect assemblies", Category = "Reflection",
                Description = "List all loaded assemblies in the application domain",
                Writeup = "Scans AppDomain.CurrentDomain and lists all loaded assemblies with their public types. Useful for discovering what's available for reflection." },
            new() { DisplayText = "/reflect stop", InsertText = "/reflect stop", Category = "Reflection",
                Description = "Stop live-watching a reflected member",
                Writeup = "Stops the live-inspection timer that was started with '/reflect <member> watch'. The watch panel is removed from the console." },
        ]);
    }

    private Func<List<string>>? _getSequenceNames;
    private Func<List<string>>? _getModelFiles;
    private Func<List<ReflectionService.MemberInfo>>? _getReflectionMembers;
    private Func<string, ReflectionService.MemberInfo?>? _getMethodInfo;
    private Func<string, List<ReflectionService.MemberInfo>?>? _getDrillDownMembers;

    /// <summary>
    /// Register a callback that returns current sequence names (for play/delete completion).
    /// </summary>
    public void SetSequenceProvider(Func<List<string>> provider) => _getSequenceNames = provider;

    /// <summary>
    /// Register a callback that returns available .gguf model file paths (for ai load completion).
    /// </summary>
    public void SetModelProvider(Func<List<string>> provider) => _getModelFiles = provider;

    /// <summary>
    /// Register a callback that returns discoverable reflection members.
    /// </summary>
    public void SetReflectionProvider(
        Func<List<ReflectionService.MemberInfo>> membersProvider,
        Func<string, ReflectionService.MemberInfo?> methodInfoProvider,
        Func<string, List<ReflectionService.MemberInfo>?>? drillDownProvider = null)
    {
        _getReflectionMembers = membersProvider;
        _getMethodInfo = methodInfoProvider;
        _getDrillDownMembers = drillDownProvider;
    }

    /// <summary>
    /// Get completion items for the current input text and caret position.
    /// Returns an empty list if no completions are relevant.
    /// </summary>
    public List<CompletionItem> GetCompletions(string text, int caretIndex)
    {
        if (string.IsNullOrEmpty(text) || caretIndex < 0)
            return [];

        // Use text up to the caret for matching
        string input = caretIndex <= text.Length ? text[..caretIndex] : text;
        string lower = input.ToLowerInvariant();

        // --- Parameter-level completions ---

        // "/play <name>" — complete sequence names
        if (lower.StartsWith("/play "))
            return GetSequenceCompletions(input, "/play ");

        // "/delete <name>"
        if (lower.StartsWith("/delete "))
            return GetSequenceCompletions(input, "/delete ");

        foreach (var spec in PathCommandSpecs)
        {
            if (lower.StartsWith(spec.Prefix))
                return GetPathCommandCompletions(input, spec);
        }

        // "/ai temp <val>" — suggest common values
        if (lower.StartsWith("/ai temp "))
            return GetTemperatureCompletions(input);

        // "/reflect <member> [args...]" — reflection member + parameter completion
        if (lower.StartsWith("/reflect "))
            return GetReflectionCompletions(input, "/reflect ");

        // --- File system path detection anywhere in input ---
        // If the user is typing what looks like a file path, offer file system completions
        string? pathSegment = ExtractTrailingPath(input);
        if (pathSegment != null)
            return GetFileSystemCompletions(pathSegment, input);

        // --- Top-level command completions ---
        CompletionItem[] snapshot;
        lock (_lock) snapshot = [.. _commands];

        if (input.Length == 0)
            return [.. snapshot];

        var results = new List<CompletionItem>();
        foreach (var cmd in snapshot)
        {
            // Match against display text or insert text
            if (cmd.InsertText.StartsWith(lower, StringComparison.OrdinalIgnoreCase)
                && !cmd.InsertText.TrimEnd().Equals(lower.TrimEnd(), StringComparison.OrdinalIgnoreCase))
            {
                results.Add(cmd);
            }
            else if (cmd.DisplayText.Contains(lower.TrimStart('/'), StringComparison.OrdinalIgnoreCase)
                && !cmd.InsertText.TrimEnd().Equals(lower.TrimEnd(), StringComparison.OrdinalIgnoreCase))
            {
                results.Add(cmd);
            }
        }

        return results;
    }

    /// <summary>
    /// Apply a completion item to the current input, returning the new text and caret position.
    /// </summary>
    public (string newText, int caretIndex) ApplyCompletion(string currentText, int caretIndex, CompletionItem item)
    {
        string input = caretIndex <= currentText.Length ? currentText[..caretIndex] : currentText;
        string suffix = caretIndex < currentText.Length ? currentText[caretIndex..] : "";

        if (item.BrowseTarget != BrowseTargetKind.None)
            return (currentText, caretIndex);

        // For file system items, replace just the path segment
        string? pathSegment = ExtractTrailingPath(input);
        if (pathSegment != null && (item.IsDirectory || item.Category == "File"))
        {
            string prefix = input[..^pathSegment.Length];
            string newText = prefix + item.InsertText + suffix;
            return (newText, prefix.Length + item.InsertText.Length);
        }

        // For parameter completions (play, delete recording, ai load, /reflect), 
        // find the command prefix and replace after it
        string lower = input.ToLowerInvariant();
        string[] paramCommands = [
            "/play ", "/delete ", "/ai load ", "/voice load ", "/caption ",
            "/fs read ", "/fs write ", "/fs watch ", "/fs unwatch ", "/fs search ",
            "/reflect "
        ];
        foreach (var cmdPrefix in paramCommands)
        {
            if (lower.StartsWith(cmdPrefix))
            {
                string newText = item.InsertText + suffix;
                return (newText, item.InsertText.Length);
            }
        }

        // For top-level commands, replace everything
        {
            string newText = item.InsertText + suffix;
            return (newText, item.InsertText.Length);
        }
    }

    // --- Private helpers ---

    private List<CompletionItem> GetReflectionCompletions(string input, string prefix)
    {
        string afterPrefix = input.Length > prefix.Length ? input[prefix.Length..] : "";
        var parts = afterPrefix.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Load assemblies for matching
        List<ReflectionService.AssemblyInfo> assemblies;
        try
        {
            assemblies = ReflectionService.GetLoadedAssemblies();
        }
        catch
        {
            assemblies = [];
        }

        // Special-case: "/reflect assemblies" — provide assembly + namespace completions
        if (afterPrefix.StartsWith("assemblies", StringComparison.OrdinalIgnoreCase))
        {
            // remainder after the keyword
            var remainder = afterPrefix.Length > "assemblies".Length ? afterPrefix["assemblies".Length..].Trim() : "";

            // No further text: list assemblies
            if (string.IsNullOrWhiteSpace(remainder))
            {
                var results = new List<CompletionItem>();
                foreach (var asm in assemblies.Take(50))
                {
                    results.Add(new CompletionItem
                    {
                        DisplayText = $"{asm.Name} ({asm.PublicTypes.Count} types)",
                        InsertText = prefix + "assemblies " + asm.Name,
                        Description = asm.Location,
                        Category = "Reflection"
                    });
                }
                return results;
            }

            // If the user typed an assembly name (and optionally a namespace fragment),
            // try to match the assembly and surface its namespaces
            var tokens = remainder.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var asmToken = tokens.Length > 0 ? tokens[0] : "";
            var nsPrefix = tokens.Length > 1 ? tokens[1] : "";

            var match = assemblies.FirstOrDefault(a =>
                a.Name.Equals(asmToken, StringComparison.OrdinalIgnoreCase)
                || a.Name.IndexOf(asmToken, StringComparison.OrdinalIgnoreCase) >= 0);

            if (match != null)
            {
                var namespaces = match.PublicTypes
                    .Select(t => {
                        int i = t.LastIndexOf('.');
                        return i > 0 ? t[..i] : "";
                    })
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n)
                    .Where(n => string.IsNullOrEmpty(nsPrefix) || n.IndexOf(nsPrefix, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Take(200);

                var results = new List<CompletionItem>();
                foreach (var ns in namespaces)
                {
                    results.Add(new CompletionItem
                    {
                        DisplayText = ns,
                        InsertText = prefix + "assemblies " + match.Name + " " + ns,
                        Description = $"Namespace in {match.Name}",
                        Category = "Reflection"
                    });
                }
                return results;
            }

            return [];
        }

        // Assembly name matching: check if first token matches an assembly name
        string firstToken = parts.Length > 0 ? parts[0] : "";
        bool isFirstTokenAssembly = false;
        ReflectionService.AssemblyInfo? matchedAssembly = null;
        string namespaceOrTypePrefix = "";

        // Try to match assembly name (exact, prefix, or first part of dot-separated)
        if (!string.IsNullOrEmpty(firstToken)) {
            // First try exact match
            matchedAssembly = assemblies.FirstOrDefault(a =>
                a.Name.Equals(firstToken, StringComparison.OrdinalIgnoreCase));

            // Try prefix match
            if (matchedAssembly == null) {
                matchedAssembly = assemblies.FirstOrDefault(a =>
                    a.Name.StartsWith(firstToken, StringComparison.OrdinalIgnoreCase));
            }

            // If firstToken contains a dot, try matching the first part as assembly
            if (matchedAssembly == null && firstToken.Contains('.')) {
                string[] dotParts = firstToken.Split('.');
                string asmPart = dotParts[0];

                matchedAssembly = assemblies.FirstOrDefault(a =>
                    a.Name.Equals(asmPart, StringComparison.OrdinalIgnoreCase)
                    || a.Name.StartsWith(asmPart, StringComparison.OrdinalIgnoreCase));

                if (matchedAssembly != null) {
                    // Everything after the assembly name is the namespace/type prefix
                    namespaceOrTypePrefix = string.Join(".", dotParts.Skip(1));
                }
            }
        }

        // If assembly matched and user is still typing (no trailing space)
        if (matchedAssembly != null && !afterPrefix.EndsWith(' ') && parts.Length == 1)
        {
            isFirstTokenAssembly = true;

            // Filter types by the namespace/type prefix
            var matchingTypes = matchedAssembly.PublicTypes
                .Where(t => string.IsNullOrEmpty(namespaceOrTypePrefix) || 
                           t.StartsWith(namespaceOrTypePrefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t)
                .Take(50);

            var results = new List<CompletionItem>();
            foreach (var type in matchingTypes)
            {
                results.Add(new CompletionItem
                {
                    DisplayText = type,
                    InsertText = prefix + matchedAssembly.Name + "." + type,
                    Description = $"Type in {matchedAssembly.Name}",
                    Category = "Reflection"
                });
            }
            return results;
        }
        else if (matchedAssembly != null && afterPrefix.EndsWith(' ') && parts.Length == 1)
        {
            // User typed assembly name and hit space — show namespaces/types from this assembly
            isFirstTokenAssembly = true;
            var namespaces = matchedAssembly.PublicTypes
                .Select(t => {
                    int i = t.LastIndexOf('.');
                    return i > 0 ? t[..i] : "";
                })
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n)
                .Take(200);

            var results = new List<CompletionItem>();
            foreach (var ns in namespaces)
            {
                results.Add(new CompletionItem
                {
                    DisplayText = $"?? {ns}",
                    InsertText = prefix + matchedAssembly.Name + "." + ns,
                    Description = $"Namespace in {matchedAssembly.Name}",
                    Category = "Reflection"
                });
            }
            return results;
        }

        if (!afterPrefix.EndsWith(' ') && parts.Length == 1 && !firstToken.Contains('.'))
        {
            // User is still typing the first token (assembly name candidate, no dots yet)
            // Check if it matches any assembly prefix
            var matches = assemblies
                .Where(a => string.IsNullOrEmpty(firstToken) || a.Name.StartsWith(firstToken, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count > 0)
            {
                // Return assembly name completions
                var results = new List<CompletionItem>();
                foreach (var asm in matches.Take(50))
                {
                    results.Add(new CompletionItem
                    {
                        DisplayText = $"?? {asm.Name}",
                        InsertText = prefix + asm.Name,
                        Description = $"{asm.PublicTypes.Count} types — {asm.Location}",
                        Category = "Reflection"
                    });
                }
                return results;
            }
        }

        // If no member typed yet, or still typing the member name — show all matching members
        if (!isFirstTokenAssembly && (parts.Length == 0 || (parts.Length == 1 && !afterPrefix.EndsWith(' '))))
        {
            string typed = parts.Length > 0 ? parts[0] : "";

            // Check for dot-path drill-down (e.g., "_DataStore." or "_DataStore.Some")
            if (typed.Contains('.') && _getDrillDownMembers != null)
            {
                int lastDot = typed.LastIndexOf('.');
                string parentPath = typed[..lastDot];
                string childTyped = typed[(lastDot + 1)..];
                var drillMembers = _getDrillDownMembers(parentPath);
                if (drillMembers != null && drillMembers.Count > 0)
                    return GetReflectionMemberList(childTyped, prefix, parentPath + ".", drillMembers);
            }

            return GetReflectionMemberList(typed, prefix);
        }

        // If first token is assembly, don't continue to member name processing
        if (isFirstTokenAssembly)
            return [];

        // Member name is complete (has a space after it) — show parameter completions or action hints
        string memberName = parts[0];

        // Offer "watch", "set" action hints when only the member is typed + space
        if (parts.Length == 1 && afterPrefix.EndsWith(' '))
        {
            var actionHints = new List<CompletionItem>();
            actionHints.Add(new CompletionItem
            {
                DisplayText = "?? watch",
                InsertText = prefix + memberName + " watch",
                Description = "Live-inspect this member",
                Category = "Reflection"
            });
            actionHints.Add(new CompletionItem
            {
                DisplayText = "? set <value>",
                InsertText = prefix + memberName + " set ",
                Description = "Set this member's value",
                Category = "Reflection"
            });

            // Also show method parameter completions if it's a method
            var methodInfo = _getMethodInfo?.Invoke(memberName);
            if (methodInfo?.Parameters != null && methodInfo.Parameters.Length > 0)
            {
                var param = methodInfo.Parameters[0];
                string currentArg = "";
                string prevArgs = "";
                actionHints.AddRange(GetReflectionParamCompletions(param, currentArg, prefix + memberName + " ", prevArgs));
            }
            return actionHints;
        }

        var methodInfoLookup = _getMethodInfo?.Invoke(memberName);

        if (methodInfoLookup?.Parameters == null || methodInfoLookup.Parameters.Length == 0)
            return []; // not a method or no params

        // Determine which parameter index we're on
        int paramIndex = parts.Length - 1 - 1; // parts[0] is member, parts[1..] are args
        if (afterPrefix.EndsWith(' '))
            paramIndex = parts.Length - 1;

        if (paramIndex < 0 || paramIndex >= methodInfoLookup.Parameters.Length)
            return [];

        var paramDetail = methodInfoLookup.Parameters[paramIndex];
        string currentArgVal = afterPrefix.EndsWith(' ') ? "" : (parts.Length > 1 ? parts[^1] : "");

        // Check for file-system path being typed in the current argument
        string? pathSegment = ExtractTrailingPath(currentArgVal);
        if (pathSegment != null)
            return GetFileSystemCompletions(pathSegment, input);

        // Build prevArgs from all already-completed argument values
        string prevArgsVal;
        if (afterPrefix.EndsWith(' '))
        {
            prevArgsVal = parts.Length > 1 ? string.Join(" ", parts[1..]) + " " : "";
        }
        else
        {
            prevArgsVal = parts.Length > 2 ? string.Join(" ", parts[1..^1]) + " " : "";
        }

        return GetReflectionParamCompletions(paramDetail, currentArgVal, prefix + memberName + " ", prevArgsVal);
    }

    private List<CompletionItem> GetReflectionMemberList(string typed, string prefix,
        string pathPrefix = "", List<ReflectionService.MemberInfo>? overrideMembers = null)
    {
        var members = overrideMembers ?? _getReflectionMembers?.Invoke() ?? [];
        if (members.Count == 0) return [];

        var results = new List<CompletionItem>();
        foreach (var member in members)
        {
            if (!string.IsNullOrEmpty(typed) &&
                !member.Name.Contains(typed, StringComparison.OrdinalIgnoreCase))
                continue;

            string icon = member.Kind switch
            {
                "Property" => "\uD83D\uDD39",
                "Field" => "\uD83D\uDD38",
                "Method" => "\u26A1",
                _ => "\u25C6"
            };

            string desc = member.Kind switch
            {
                "Method" => $"{member.ReturnType} {member.Description}",
                "Property" when !string.IsNullOrEmpty(member.Description) => $"{member.ReturnType} — {member.Description}",
                "Field" when !string.IsNullOrEmpty(member.Description) => $"{member.ReturnType} — {member.Description}",
                _ => $"{member.ReturnType} ({member.Description})"
            };

            string writeup = member.Kind switch
            {
                "Method" => $"Callable method returning {member.ReturnType}. {(member.Parameters is { Length: > 0 } ? $"Parameters: {string.Join(", ", member.Parameters.Select(p => $"{p.Type} {p.Name}"))}" : "No parameters.")}",
                "Property" => $"{(member.IsReadOnly ? "Read-only" : "Read/write")} property of type {member.ReturnType}.{(!string.IsNullOrEmpty(member.Description) ? $" {member.Description}" : "")}",
                "Field" => $"{(member.IsReadOnly ? "Read-only" : "Mutable")} field of type {member.ReturnType}.{(!string.IsNullOrEmpty(member.Description) ? $" {member.Description}" : "")}",
                _ => member.Description
            };

            // Methods get a trailing space so parameter completions trigger
            string insertSuffix = member.Kind == "Method" && member.Parameters is { Length: > 0 }
                ? " " : "";

            results.Add(new CompletionItem
            {
                DisplayText = $"{icon} {member.Name}",
                InsertText = prefix + pathPrefix + member.Name + insertSuffix,
                Description = desc,
                Writeup = writeup,
                Category = "Reflection",
                IsExecutable = member.Kind == "Method"
            });

            if (results.Count >= 50) break;
        }
        return results;
    }

    private List<CompletionItem> GetReflectionParamCompletions(
        ReflectionService.ParameterDetail param, string currentArg,
        string commandPrefix, string prevArgs)
    {
        var results = new List<CompletionItem>();
        string fullPrefix = commandPrefix + prevArgs;

        // If the parameter is file-like, show a ".." entry to open file browser
        if (param.IsFileLike)
        {
            if (string.IsNullOrEmpty(currentArg) || "..".StartsWith(currentArg))
            {
                results.Add(new CompletionItem
                {
                    DisplayText = "Browse file",
                    InsertText = fullPrefix,
                    Description = $"Browse for {param.Name} ({param.Type})",
                    Category = "Browse",
                    HasBrowseButton = true,
                    BrowseTarget = BrowseTargetKind.File,
                    BrowseTitle = $"Select {param.Name}",
                    BrowseFilter = "All files|*.*",
                    BrowseInsertPrefix = fullPrefix
                });
            }

            // Also show common drive roots
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;
                string root = drive.RootDirectory.FullName;
                if (string.IsNullOrEmpty(currentArg) || root.StartsWith(currentArg, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new CompletionItem
                    {
                        DisplayText = $"\uD83D\uDCBE {root}",
                        InsertText = root,
                        Description = $"{drive.DriveType}",
                        Category = "File",
                        IsDirectory = true
                    });
                }
            }
            return results;
        }

        // Bool parameter
        if (param.Type == "bool" || param.Type == "bool?")
        {
            foreach (var val in new[] { "true", "false" })
            {
                if (string.IsNullOrEmpty(currentArg) || val.StartsWith(currentArg, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new CompletionItem
                    {
                        DisplayText = val,
                        InsertText = fullPrefix + val,
                        Description = $"{param.Name}",
                        Category = "Reflection"
                    });
                }
            }
            return results;
        }

        // Show parameter hint
        string defaultInfo = param.IsOptional && param.DefaultValue != null
            ? $" = {param.DefaultValue}" : "";
        results.Add(new CompletionItem
        {
            DisplayText = $"\u2139 {param.Name}: {param.Type}{defaultInfo}",
            InsertText = fullPrefix + (param.DefaultValue ?? ""),
            Description = param.IsOptional ? "optional" : "required",
            Category = "Reflection"
        });

        return results;
    }

    private List<CompletionItem> GetPathCommandCompletions(string input, PathCommandSpec spec)
    {
        if (!TryGetActivePathArgument(input, spec.Prefix, spec.FirstArgumentOnly, out var currentArg, out var insertPrefix))
            return [];

        currentArg = currentArg.Trim().Trim('"');

        List<string> models = spec.Prefix.Equals("/ai load ", StringComparison.OrdinalIgnoreCase)
            ? _getModelFiles?.Invoke() ?? []
            : [];
        string initialDirectoryOverride = "";
        if (string.IsNullOrWhiteSpace(currentArg) && models.Count > 0)
            initialDirectoryOverride = Path.GetDirectoryName(models[0]) ?? "";

        var results = new List<CompletionItem>
        {
            CreateBrowseItem(spec, insertPrefix, currentArg, initialDirectoryOverride)
        };

        if (spec.Prefix.Equals("/ai load ", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(currentArg))
        {
            foreach (var model in models)
            {
                string name = Path.GetFileName(model);
                results.Add(new CompletionItem
                {
                    DisplayText = name,
                    InsertText = spec.Prefix + model,
                    Description = "GGUF model",
                    Category = "File"
                });
            }
            return results;
        }

        if (!string.IsNullOrWhiteSpace(currentArg))
            results.AddRange(GetFileSystemCompletions(currentArg, input, spec.Filter, spec.Target));

        return results;
    }

    private static CompletionItem CreateBrowseItem(
        PathCommandSpec spec,
        string insertPrefix,
        string currentArg,
        string initialDirectoryOverride = "")
    {
        bool folder = spec.Target == BrowseTargetKind.Directory;
        return new CompletionItem
        {
            DisplayText = folder ? "Browse folder" : "Browse file",
            InsertText = insertPrefix,
            Description = folder ? "Open folder dialog" : "Open file dialog",
            Category = "Browse",
            HasBrowseButton = true,
            BrowseTarget = spec.Target,
            BrowseTitle = spec.Title,
            BrowseFilter = spec.Filter,
            BrowseInsertPrefix = insertPrefix,
            BrowseInitialDirectory = !string.IsNullOrWhiteSpace(initialDirectoryOverride)
                ? initialDirectoryOverride
                : GetInitialDirectoryFromPathInput(currentArg)
        };
    }

    private static bool TryGetActivePathArgument(
        string input,
        string prefix,
        bool firstArgumentOnly,
        out string currentArg,
        out string insertPrefix)
    {
        currentArg = "";
        insertPrefix = prefix;

        if (input.Length <= prefix.Length)
            return true;

        string afterPrefix = input[prefix.Length..];
        int leading = afterPrefix.Length - afterPrefix.TrimStart().Length;
        string argText = afterPrefix[leading..];
        insertPrefix = input[..(prefix.Length + leading)];

        if (!firstArgumentOnly)
        {
            currentArg = argText.StartsWith('"') ? argText[1..] : argText;
            return true;
        }

        if (string.IsNullOrEmpty(argText))
            return true;

        if (argText[0] == '"')
        {
            int closingQuote = argText.IndexOf('"', 1);
            if (closingQuote < 0)
            {
                currentArg = argText[1..];
                return true;
            }

            string rest = argText[(closingQuote + 1)..];
            if (!string.IsNullOrEmpty(rest))
                return false;

            currentArg = argText[1..closingQuote];
            return true;
        }

        int firstSpace = argText.IndexOfAny([' ', '\t']);
        if (firstSpace >= 0)
            return false;

        currentArg = argText;
        return true;
    }

    private static string GetInitialDirectoryFromPathInput(string pathInput)
    {
        try
        {
            pathInput = pathInput.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(pathInput))
                return "";

            if (Directory.Exists(pathInput))
                return pathInput;

            if (pathInput.Length == 2 && pathInput[1] == ':')
            {
                string root = pathInput + "\\";
                return Directory.Exists(root) ? root : "";
            }

            string? dir = Path.GetDirectoryName(pathInput);
            return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) ? dir : "";
        }
        catch
        {
            return "";
        }
    }

    private List<CompletionItem> GetSequenceCompletions(string input, string prefix)
    {
        var names = _getSequenceNames?.Invoke() ?? [];
        if (names.Count == 0) return [];

        string typed = input.Length > prefix.Length ? input[prefix.Length..] : "";
        string typedLower = typed.ToLowerInvariant();

        var results = new List<CompletionItem>();
        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(typedLower) || name.Contains(typedLower, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new CompletionItem
                {
                    DisplayText = name,
                    InsertText = prefix + name,
                    Description = "Saved sequence",
                    Category = "Recording"
                });
            }
        }
        return results;
    }

    private List<CompletionItem> GetFileCompletions(string input, string prefix, string filter)
    {
        string typed = input.Length > prefix.Length ? input[prefix.Length..].TrimStart('"') : "";

        // First offer model files from the models directory
        if (string.IsNullOrEmpty(typed))
        {
            var models = _getModelFiles?.Invoke() ?? [];
            var results = new List<CompletionItem>();
            foreach (var model in models)
            {
                string name = Path.GetFileName(model);
                results.Add(new CompletionItem
                {
                    DisplayText = name,
                    InsertText = prefix + model,
                    Description = "GGUF model",
                    Category = "File"
                });
            }
            return results;
        }

        // If they're typing a path, browse file system
        return GetFileSystemCompletions(typed, input, filter);
    }

    private static List<CompletionItem> GetTemperatureCompletions(string input)
    {
        string[] values = ["0.0", "0.1", "0.3", "0.5", "0.7", "1.0", "1.2", "1.5", "2.0"];
        string prefix = "/ai temp ";
        string typed = input.Length > prefix.Length ? input[prefix.Length..] : "";
        var results = new List<CompletionItem>();
        foreach (var v in values)
        {
            if (string.IsNullOrEmpty(typed) || v.StartsWith(typed))
            {
                results.Add(new CompletionItem
                {
                    DisplayText = v,
                    InsertText = "/ai temp " + v,
                    Description = v switch
                    {
                        "0.0" => "Deterministic",
                        "0.1" or "0.3" => "Very focused",
                        "0.5" => "Balanced-focused",
                        "0.7" => "Default / balanced",
                        "1.0" => "Creative",
                        "1.2" or "1.5" => "Very creative",
                        "2.0" => "Maximum creativity",
                        _ => ""
                    },
                    Category = "AI"
                });
            }
        }
        return results;
    }

    /// <summary>
    /// Extract a trailing file path from the input (e.g. "ai load C:\Users" ? "C:\Users").
    /// Returns null if no path-like segment is found.
    /// </summary>
    private static string? ExtractTrailingPath(string input)
    {
        // Look for drive letter pattern or UNC path
        // Find the last occurrence of a drive letter pattern like X:\ or X:/
        for (int i = input.Length - 1; i >= 1; i--)
        {
            if (input[i] == ':' && i > 0 && char.IsLetter(input[i - 1]))
            {
                // Found a drive letter — return everything from here to end
                string candidate = input[(i - 1)..];
                if (candidate.Length >= 2) // At least "C:"
                    return candidate;
            }
        }

        // Check for UNC path
        if (input.Contains(@"\\"))
        {
            int uncIdx = input.LastIndexOf(@"\\", StringComparison.Ordinal);
            return input[uncIdx..];
        }

        return null;
    }

    private static List<CompletionItem> GetFileSystemCompletions(
        string pathInput,
        string fullInput,
        string? filter = null,
        BrowseTargetKind target = BrowseTargetKind.None)
    {
        var results = new List<CompletionItem>();

        try
        {
            string dir;
            string partial;

            // Determine directory and partial filename
            if (pathInput.EndsWith('\\') || pathInput.EndsWith('/'))
            {
                dir = pathInput;
                partial = "";
            }
            else
            {
                dir = Path.GetDirectoryName(pathInput) ?? "";
                partial = Path.GetFileName(pathInput);
            }

            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                // Maybe just "C:" without trailing slash
                if (pathInput.Length == 2 && pathInput[1] == ':')
                {
                    dir = pathInput + "\\";
                    partial = "";
                }
                else
                {
                    return results;
                }
            }

            // Directories first
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                string name = Path.GetFileName(d);
                if (string.IsNullOrEmpty(partial) || name.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new CompletionItem
                    {
                        DisplayText = "\uD83D\uDCC1 " + name,
                        InsertText = d + "\\",
                        Description = "Directory",
                        Category = "File",
                        IsDirectory = true
                    });
                }

                if (results.Count >= 50) break;
            }

            if (target != BrowseTargetKind.Directory)
            {
                // Files
                var fileEnum = string.IsNullOrEmpty(filter)
                    ? Directory.EnumerateFiles(dir)
                    : Directory.EnumerateFiles(dir, filter);

                foreach (var f in fileEnum)
                {
                    string name = Path.GetFileName(f);
                    if (string.IsNullOrEmpty(partial) || name.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                    {
                        string ext = Path.GetExtension(f).ToLowerInvariant();
                        string icon = ext switch
                        {
                            ".gguf" => "\uD83E\uDDE0",
                            ".exe" => "\u2699",
                            ".dll" => "\uD83D\uDD17",
                            ".json" => "\uD83D\uDCCB",
                            ".txt" or ".md" or ".log" => "\uD83D\uDCC4",
                            ".png" or ".jpg" or ".bmp" or ".gif" => "\uD83D\uDDBC",
                            _ => "\uD83D\uDCC4"
                        };

                        results.Add(new CompletionItem
                        {
                            DisplayText = $"{icon} {name}",
                            InsertText = f,
                            Description = FormatFileSize(f),
                            Category = "File"
                        });
                    }

                    if (results.Count >= 50) break;
                }
            }
        }
        catch
        {
            // Access denied, invalid path, etc. — silently return empty
        }

        return results;
    }

    private static string FormatFileSize(string path)
    {
        try
        {
            long bytes = new FileInfo(path).Length;
            return bytes switch
            {
                < 1024 => $"{bytes} B",
                < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
                < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
                _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
            };
        }
        catch { return ""; }
    }
}
