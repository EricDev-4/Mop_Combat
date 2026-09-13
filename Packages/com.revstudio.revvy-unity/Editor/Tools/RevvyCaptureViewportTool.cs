using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace RevStudio.Revvy.Editor
{
    /// <summary>
    /// Tier-6 viewport screenshot (docs/design/tier6-screenshot-contract.md).
    ///
    /// Renders the last active Scene view to a PNG under the project's
    /// <c>.revvy/captures/</c> directory and returns a project-relative path.
    ///
    /// The load-bearing decision here is the headless policy. With
    /// <c>-nographics</c> Unity installs a null graphics device that does not throw,
    /// does not fail, and happily produces a valid uniformly-grey PNG — a picture an
    /// agent would describe as if it were the user's scene. Measured on 6000.3.8f1: a
    /// lit cube rendered through a Scene view camera gives 1540 distinct colours with
    /// a real device and exactly 1 with the null device. So this tool refuses rather
    /// than degrading; see contract §1.
    /// </summary>
    public static class RevvyCaptureViewportTool
    {
        private const int MinimumDimension = 64;
        private const int MaximumDimension = 4096;
        private const int DefaultDimension = 1024;

        /// <summary>Contract §6: a PNG above this is refused rather than silently re-encoded.</summary>
        private const int MaximumByteLength = 4 * 1024 * 1024;

        /// <summary>
        /// Tier-10 §5: a much lower ceiling for inline images. Base64 inflates by 4/3 and
        /// the bytes cross the MCP transport into a model's context, so the file limit
        /// would be far too generous here.
        /// </summary>
        private const int InlineMaximumByteLength = 1024 * 1024;

        /// <summary>Contract §6: retained capture files, oldest deleted first.</summary>
        private const int RetainedCaptures = 20;

        private const string CaptureDirectory = ".revvy/captures";
        private const string CapturePrefix = "viewport-";
        private const string CaptureExtension = ".png";

        [RevvyTool(
            "capture_viewport",
            "Capture the editor viewport of the currently edited scene to a PNG file inside the " +
            "project and return its path.",
            Title = "Capture viewport",
            ReadOnly = true,
            Idempotent = true,
            StrictSchema = true,
            DefaultErrorCode = "editor_api_failure")]
        public static RevvyJson CaptureViewport(
            [RevvyToolParam(
                "Maximum length of the longer image edge in pixels. The capture is downscaled to " +
                "fit, preserving aspect ratio; it is never upscaled.",
                SchemaJson = "{\"type\":\"integer\",\"minimum\":64,\"maximum\":4096}")]
            int max_dimension = DefaultDimension,
            [RevvyToolParam(
                "Also return the PNG as a base64 image content block so the caller can see it " +
                "without reading the file. Subject to a smaller size limit than the file.")]
            bool inline = false)
        {
            try
            {
                // Belt and braces: the dispatcher already enforces the schema bounds for a
                // StrictSchema tool, so this only fires for a caller reaching the method
                // some other way (contract §4).
                if (max_dimension < MinimumDimension || max_dimension > MaximumDimension)
                {
                    throw CaptureFailure(
                        "invalid_argument",
                        "max_dimension must be between " + MinimumDimension + " and " +
                        MaximumDimension + ".");
                }

                if (PrefabStageUtility.GetCurrentPrefabStage() != null)
                {
                    throw CaptureFailure(
                        "no_edited_scene",
                        "Viewport capture is unavailable while editing a Prefab Stage.");
                }

                Scene scene = SceneManager.GetActiveScene();
                if (!scene.IsValid() || !scene.isLoaded || EditorSceneManager.IsPreviewScene(scene))
                {
                    throw CaptureFailure("no_edited_scene", "There is no currently edited scene.");
                }

                // Contract §1: refuse rather than hand back a null-device placeholder.
                if (IsNullDevice())
                {
                    throw ViewportUnavailable(
                        "The editor has no graphics device (running headless), so no real pixels " +
                        "can be captured. Run the editor with a display to use capture_viewport.");
                }

                // Never opened by the bridge: opening a window is an editor state change and
                // would break the read-only annotation (contract §2, §10).
                SceneView sceneView = SceneView.lastActiveSceneView;
                if (sceneView == null || sceneView.camera == null)
                {
                    throw ViewportUnavailable(
                        "No Scene view is open, so there is no editor viewport to capture. " +
                        "Open a Scene view and retry.");
                }

                int sourceWidth;
                int sourceHeight;
                MeasureViewport(sceneView, out sourceWidth, out sourceHeight);

                int savedWidth;
                int savedHeight;
                ScaleToFit(sourceWidth, sourceHeight, max_dimension, out savedWidth, out savedHeight);

                byte[] png = RenderToPng(sceneView.camera, savedWidth, savedHeight);
                if (png == null || png.Length == 0)
                {
                    throw CaptureFailure("editor_api_failure", "Unity produced an empty PNG buffer.");
                }

                if (png.Length > MaximumByteLength)
                {
                    throw CaptureFailure(
                        "capture_too_large",
                        "The encoded capture is " + png.Length + " bytes, above the " +
                        MaximumByteLength + " byte limit. Lower max_dimension and retry; the " +
                        "bridge does not silently re-encode at a size you did not ask for.");
                }

                // Tier-10 §5: the inline limit is separate from and lower than the file
                // limit, because base64 inflates by 4/3 and the result crosses the MCP
                // transport rather than sitting on disk. Checked before the file is
                // written so an oversized inline request cannot leave a capture behind
                // that the caller never learns about.
                if (inline && png.Length > InlineMaximumByteLength)
                {
                    throw CaptureFailure(
                        "capture_too_large",
                        "The capture is " + png.Length + " bytes, above the " +
                        InlineMaximumByteLength + " byte limit for inline images (base64 inflates " +
                        "it by a further third). Lower max_dimension, or omit inline and read " +
                        "the file instead.");
                }

                string relativePath = WriteCapture(png);

                RevvyJson result = RevvyJson.Object()
                    .Set("success", true)
                    .Set("engine", RevvyEnv.EngineId)
                    .Set("format", "png")
                    .Set("file_path", relativePath)
                    .Set("byte_length", png.Length)
                    .Set("width", sourceWidth)
                    .Set("height", sourceHeight)
                    .Set("saved_width", savedWidth)
                    .Set("saved_height", savedHeight)
                    .Set("max_dimension", max_dimension)
                    .Set("downscaled", savedWidth != sourceWidth || savedHeight != sourceHeight)
                    .Set("play_mode", EditorApplication.isPlaying)
                    .Set("renderer", DescribeRenderer())
                    .Set("inline", inline)
                    .Set("scene", RevvyJson.Object()
                        .Set("name", scene.name ?? string.Empty)
                        .Set("path", scene.path ?? string.Empty)
                        .Set("saved", !string.IsNullOrEmpty(scene.path)));

                if (inline)
                {
                    // The bytes are the exact same PNG that was written, so a caller can
                    // verify the two against each other (contract §5).
                    result.Set(RevvyMcpHandler.AttachmentsKey, RevvyJson.Array()
                        .Add(RevvyJson.Object()
                            .Set("type", "image")
                            .Set("mimeType", "image/png")
                            .Set("data", Convert.ToBase64String(png))));
                }

                return result;
            }
            catch (RevvyToolException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new RevvyToolException(
                    "Viewport capture failed: " + RevvyLog.Describe(exception),
                    "editor_api_failure",
                    exception);
            }
        }

        // ------------------------------------------------------------------ renderer

        private static bool IsNullDevice()
        {
            return SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
        }

        /// <summary>
        /// Contract §1: emitted on success and inside the viewport_unavailable error, so a
        /// caller can tell "no display" from "no Scene view open" and instruct the user.
        /// </summary>
        internal static RevvyJson DescribeRenderer()
        {
            bool headless = IsNullDevice();
            return RevvyJson.Object()
                .Set("mode", headless ? "dummy" : "real")
                .Set("device", SystemInfo.graphicsDeviceType.ToString())
                .Set("headless", headless);
        }

        // ------------------------------------------------------------------ sizing

        private static void MeasureViewport(SceneView sceneView, out int width, out int height)
        {
            float scale = EditorGUIUtility.pixelsPerPoint;
            if (scale <= 0f)
            {
                scale = 1f;
            }

            Rect position = sceneView.position;
            width = Mathf.Max(1, Mathf.RoundToInt(position.width * scale));
            height = Mathf.Max(1, Mathf.RoundToInt(position.height * scale));
        }

        /// <summary>Downscales to fit the long edge. Never upscales (contract §4).</summary>
        private static void ScaleToFit(
            int sourceWidth,
            int sourceHeight,
            int maxDimension,
            out int width,
            out int height)
        {
            int longest = Mathf.Max(sourceWidth, sourceHeight);
            if (longest <= maxDimension)
            {
                width = sourceWidth;
                height = sourceHeight;
                return;
            }

            float scale = (float)maxDimension / longest;
            width = Mathf.Max(1, Mathf.RoundToInt(sourceWidth * scale));
            height = Mathf.Max(1, Mathf.RoundToInt(sourceHeight * scale));
        }

        // ------------------------------------------------------------------ capture

        /// <summary>
        /// Renders the Scene view camera into a temporary RenderTexture and encodes it.
        ///
        /// Both the camera's target and the global active RenderTexture are restored in a
        /// finally block: leaving either dangling would corrupt the editor's own repaint,
        /// which is exactly the kind of state change §2 forbids.
        ///
        /// This is a manual Render(), not the editor's repaint path, so grid and gizmo
        /// overlays are not guaranteed to appear (contract §10).
        /// </summary>
        private static byte[] RenderToPng(Camera camera, int width, int height)
        {
            RenderTexture target = null;
            Texture2D readback = null;
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture previousTarget = camera.targetTexture;

            try
            {
                target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                target.Create();

                camera.targetTexture = target;
                camera.Render();

                RenderTexture.active = target;
                readback = new Texture2D(width, height, TextureFormat.RGB24, false);
                readback.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
                readback.Apply();

                return readback.EncodeToPNG();
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;

                if (readback != null)
                {
                    UnityEngine.Object.DestroyImmediate(readback);
                }

                if (target != null)
                {
                    target.Release();
                    UnityEngine.Object.DestroyImmediate(target);
                }
            }
        }

        // ------------------------------------------------------------------ storage

        /// <summary>Rotates old captures, writes the new one, returns its project-relative path.</summary>
        private static string WriteCapture(byte[] png)
        {
            string projectRoot = ResolveProjectRoot();
            string directory = Path.Combine(projectRoot, CaptureDirectory.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(directory);

            RotateCaptures(directory);

            string fileName = BuildFileName(directory);
            File.WriteAllBytes(Path.Combine(directory, fileName), png);

            // Always forward slashes and always relative: an absolute path leaks the
            // machine layout and is not portable to the caller (contract §6).
            return CaptureDirectory + "/" + fileName;
        }

        private static string ResolveProjectRoot()
        {
            DirectoryInfo parent = Directory.GetParent(Application.dataPath);
            return parent != null ? parent.FullName : Application.dataPath;
        }

        /// <summary>
        /// Deletes oldest captures until at most <see cref="RetainedCaptures"/> - 1 remain.
        /// Only files matching the capture name pattern are considered, so an unrelated
        /// file dropped in the directory is never touched (contract §6).
        /// </summary>
        private static void RotateCaptures(string directory)
        {
            List<FileInfo> captures;
            try
            {
                captures = new List<FileInfo>(
                    new DirectoryInfo(directory).GetFiles(CapturePrefix + "*" + CaptureExtension));
            }
            catch (Exception exception)
            {
                RevvyLog.Verbose("Capture rotation skipped: " + RevvyLog.Describe(exception));
                return;
            }

            if (captures.Count < RetainedCaptures)
            {
                return;
            }

            captures.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
            int removeCount = captures.Count - (RetainedCaptures - 1);
            for (int i = 0; i < removeCount; i++)
            {
                try
                {
                    captures[i].Delete();
                }
                catch (Exception exception)
                {
                    RevvyLog.Verbose(
                        "Could not rotate out " + captures[i].Name + ": " + RevvyLog.Describe(exception));
                }
            }
        }

        /// <summary>
        /// `viewport-<UTC>-<counter>.png`. The counter disambiguates captures taken inside
        /// the same second; names sort chronologically as plain strings (contract §6).
        /// </summary>
        private static string BuildFileName(string directory)
        {
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            for (int counter = 1; counter < 1000; counter++)
            {
                string candidate = CapturePrefix + stamp + "-" +
                                   counter.ToString("000", CultureInfo.InvariantCulture) + CaptureExtension;
                if (!File.Exists(Path.Combine(directory, candidate)))
                {
                    return candidate;
                }
            }

            throw CaptureFailure(
                "editor_api_failure",
                "Could not find a free capture file name for " + stamp + ".");
        }

        private static RevvyToolException CaptureFailure(string code, string message)
        {
            return new RevvyToolException(message, code);
        }

        /// <summary>
        /// Contract §8: a viewport_unavailable payload must carry the renderer object, so
        /// the caller can tell "no display" from "no Scene view open" and tell the user
        /// which one to fix instead of guessing.
        /// </summary>
        private static RevvyToolException ViewportUnavailable(string message)
        {
            return new RevvyToolException(
                message,
                "viewport_unavailable",
                RevvyJson.Object().Set("renderer", DescribeRenderer()));
        }
    }
}
