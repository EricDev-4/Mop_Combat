using System;

namespace RevStudio.Revvy.Editor
{
    /// <summary>
    /// Marks a <b>public static</b> method as an MCP tool.
    ///
    /// Mirrors the Unreal side, where Revvy reflects <c>UFUNCTION</c> metadata into
    /// tool descriptors (contract §8.2). The method signature is the schema: each
    /// parameter becomes an <c>inputSchema</c> property, parameters without a default
    /// value become <c>required</c>, and the return value must be a
    /// <see cref="RevvyJson"/> object which is serialized into the MCP text content
    /// block.
    ///
    /// <code>
    /// [RevvyTool("editor_status", "Report engine and project state.", ReadOnly = true)]
    /// public static RevvyJson EditorStatus() { ... }
    /// </code>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class RevvyToolAttribute : Attribute
    {
        public RevvyToolAttribute(string name, string description)
        {
            Name = name;
            Description = description;
        }

        /// <summary>MCP tool name (snake_case, matching the UE tool naming convention).</summary>
        public string Name { get; private set; }

        public string Description { get; private set; }

        /// <summary>Human-readable title emitted in <c>annotations.title</c>.</summary>
        public string Title { get; set; }

        /// <summary><c>annotations.readOnlyHint</c> — the tool never mutates editor state.</summary>
        public bool ReadOnly { get; set; }

        /// <summary><c>annotations.destructiveHint</c> — the tool can delete or overwrite.</summary>
        public bool Destructive { get; set; }

        /// <summary><c>annotations.idempotentHint</c> — repeat calls have no extra effect.</summary>
        public bool Idempotent { get; set; }

        /// <summary><c>annotations.openWorldHint</c> — the tool reaches outside the project.</summary>
        public bool OpenWorld { get; set; }

        /// <summary>
        /// For multi-action tools (contract §3 prefers one tool with an <c>action</c>
        /// enum over many narrow tools): the subset of actions that are read-only.
        /// Emitted as <c>annotations.readOnlyActions</c>, matching the UE server.
        /// </summary>
        public string[] ReadOnlyActions { get; set; }

        /// <summary>Run the dispatch on the main thread with a custom timeout. 0 = default.</summary>
        public int TimeoutMs { get; set; }

        /// <summary>
        /// Validate supplied JSON values against the generated schema before CLR coercion.
        /// Use this for contracts where accepting string/boolean coercions would be ambiguous.
        /// </summary>
        public bool StrictSchema { get; set; }

        /// <summary>
        /// Stable tool-level code used when an unexpected dispatch, timeout, or editor API
        /// failure escapes without a more specific <see cref="RevvyToolException.ErrorCode"/>.
        /// Empty leaves the normal tool error envelope unchanged.
        /// </summary>
        public string DefaultErrorCode { get; set; }
    }

    /// <summary>
    /// Per-parameter schema metadata. Optional: an unannotated parameter still gets a
    /// type-derived schema entry, just without a description or enum constraint.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    public sealed class RevvyToolParamAttribute : Attribute
    {
        public RevvyToolParamAttribute(string description)
        {
            Description = description;
        }

        public string Description { get; private set; }

        /// <summary>Allowed values, emitted as JSON Schema <c>enum</c>.</summary>
        public string[] Enum { get; set; }

        /// <summary>
        /// Forces the parameter into <c>required</c> even though it has a C# default
        /// (used where a default exists only to keep the signature ordering legal).
        /// </summary>
        public bool Required { get; set; }

        /// <summary>
        /// Raw JSON Schema fragment replacing the type-derived one. Needed where a
        /// parameter is typed as <see cref="RevvyJson"/> (which reflects to a bare
        /// <c>{"type":"object"}</c>) but actually carries something more specific,
        /// e.g. <c>{"type":"array"}</c>. Ignored if it fails to parse.
        /// </summary>
        public string SchemaJson { get; set; }
    }
}
