namespace Assets.Scripts
{
    using System;
    using System.Collections;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using Assets.Scripts.Vizzy.UI;
    using Assets.Scripts.Vizzy.UI.Elements;
    using TMPro;
    using UnityEngine;
    using UnityEngine.UI;

    /// <summary>
    /// Saves the whole program as one PNG at the blocks' own size (the editor at zoom 1). The program's
    /// transform moves for the capture onto a world-space canvas of our own, far from the scene, where an
    /// orthographic camera renders it tile by tile into a RenderTexture; the rows go straight to the file.
    /// A blocker covers the editor meanwhile, and everything is put back when it ends, fails or the editor
    /// closes. See DESIGN.md, "Program image".
    /// </summary>
    public sealed class ProgramImage : MonoBehaviour
    {
        private const int TileWidth = 4096;
        private const int TileHeight = 1024;
        private const int TilesPerFrame = 4;
        private const float Padding = 40f;
        private const long MaxPixels = 400L * 1000 * 1000;

        // Far from anything the game draws, so our camera sees only our canvas.
        private static readonly Vector3 Origin = new Vector3(0f, -200000f, 0f);

        private static ProgramImage _running;

        private VizzyUIController _controller;
        private RectTransform _program;
        private Transform _parent;
        private int _siblingIndex;
        private Vector3 _localPosition;
        private Vector3 _localScale;
        private GameObject _captureRoot;
        private GameObject _blocker;
        private TextMeshProUGUI _blockerText;
        private RenderTexture _target;
        private Texture2D _readBack;
        private readonly byte[] _tile = new byte[TileWidth * TileHeight * 3];
        private PngStreamWriter _png;
        private FileStream _file;
        private string _path;
        private EditorCulling _culling;
        private bool _moved;

        public static void Save(VizzyUIController controller)
        {
            if (controller == null)
            {
                return;
            }

            if (_running != null)
            {
                controller.ShowMessage(Text.Get("VizzyStudio.Image.Busy"), 3f);
                return;
            }

            var image = controller.gameObject.GetComponent<ProgramImage>() ?? controller.gameObject.AddComponent<ProgramImage>();
            image._controller = controller;
            _running = image;
            image.StartCoroutine(image.Run());
        }

        private IEnumerator Run()
        {
            var total = Stopwatch.StartNew();
            var program = (_controller.VizzyUI as VizzyUIScript)?.ProgramTransform as RectTransform;
            if (program == null || !this.Try("start", () => this.Begin(program)))
            {
                yield break;
            }

            // Blocks never shown yet are built in their first active frame, and laid out in the next.
            yield return null;
            yield return null;

            Rect box = default;
            var width = 0;
            var height = 0;
            var scale = 1f;
            if (!this.Try("measure", () =>
            {
                box = Measure(program);
                var pixels = (double)box.width * box.height;
                if (pixels > MaxPixels)
                {
                    scale = (float)Math.Sqrt(MaxPixels / pixels);
                }

                width = Mathf.Max(1, Mathf.CeilToInt(box.width * scale));
                height = Mathf.Max(1, Mathf.CeilToInt(box.height * scale));
                var canvas = _controller.ProgramContainer.GetComponentInParent<Canvas>();
                var root = canvas == null ? null : canvas.rootCanvas;
                EditorLog.Debug("Image: editor canvas " + (root == null ? "none" : root.renderMode + ", scale factor " + root.scaleFactor.ToString("0.###"))
                    + ", zoom " + program.localScale.x.ToString("0.###") + ", program box " + box.width.ToString("0") + " x " + box.height.ToString("0")
                    + ", image " + width + " x " + height + " at scale " + scale.ToString("0.###") + ".");
                this.Prepare(program, box, scale, width, height);
            }))
            {
                yield break;
            }

            var columns = (width + TileWidth - 1) / TileWidth;
            var bands = (height + TileHeight - 1) / TileHeight;
            var band = new byte[width * TileHeight * 3];
            var render = new Stopwatch();
            var tilesThisFrame = 0;
            for (var row = 0; row < bands; row++)
            {
                var rowsInBand = Math.Min(TileHeight, height - (row * TileHeight));
                for (var column = 0; column < columns; column++)
                {
                    var r = row;
                    var c = column;
                    render.Start();
                    if (!this.Try("render", () => this.RenderTile(c, r, width, rowsInBand, band)))
                    {
                        yield break;
                    }

                    render.Stop();
                    if (++tilesThisFrame >= TilesPerFrame)
                    {
                        tilesThisFrame = 0;
                        this.SetProgress((row * columns) + column + 1, bands * columns);
                        yield return null;
                    }
                }

                if (!this.Try("write", () =>
                {
                    for (var y = 0; y < rowsInBand; y++)
                    {
                        _png.WriteRow(band, y * width * 3);
                    }
                }))
                {
                    yield break;
                }
            }

            if (this.Try("finish", () => _png.Finish()))
            {
                var path = _path;
                var bytes = _file.Length;
                this.End(false);
                EditorLog.Info("Saved the program image " + path + ": " + width + " x " + height + ", " + (bytes / (1024f * 1024f)).ToString("0.0") + " MB, "
                    + (bands * columns) + " tiles, rendering " + render.ElapsedMilliseconds + " ms, in all " + total.ElapsedMilliseconds + " ms.");
                _controller.ShowMessage(Text.Get("VizzyStudio.Image.Saved", path), 8f);
            }
        }

        private void Begin(RectTransform program)
        {
            _program = program;
            _culling = _controller.GetComponent<EditorCulling>();
            if (_culling != null)
            {
                _culling.Paused = true;
            }

            this.ShowBlocker();
        }

        /// <summary>Every block's box in the program's own space (as Editor Culling measures stacks).</summary>
        private static Rect Measure(RectTransform program)
        {
            var scale = program.lossyScale.x;
            var any = false;
            var box = default(Rect);
            foreach (var block in program.GetComponentsInChildren<BlockElementScript>(true))
            {
                if (block == null || block.IsDestroyed)
                {
                    continue;
                }

                var rect = block.RectTransform;
                var size = block.Size * (rect.lossyScale.x / scale);
                var corner = program.InverseTransformPoint(rect.position);
                var left = corner.x - (rect.pivot.x * size.x);
                var top = corner.y + ((1f - rect.pivot.y) * size.y);
                var one = Rect.MinMaxRect(left, top - size.y, left + size.x, top);
                box = any ? Rect.MinMaxRect(Mathf.Min(box.xMin, one.xMin), Mathf.Min(box.yMin, one.yMin), Mathf.Max(box.xMax, one.xMax), Mathf.Max(box.yMax, one.yMax)) : one;
                any = true;
            }

            if (!any)
            {
                throw new InvalidOperationException("the program has no blocks");
            }

            return Rect.MinMaxRect(box.xMin - Padding, box.yMin - Padding, box.xMax + Padding, box.yMax + Padding);
        }

        private void Prepare(RectTransform program, Rect box, float scale, int width, int height)
        {
            var folder = Path.Combine(Path.GetDirectoryName(VizzyUIScript.FlightProgramsFolderPath.TrimEnd('/', '\\')), "Program Images");
            Directory.CreateDirectory(folder);
            var name = (_controller.VizzyUI as VizzyUIScript)?.FlightProgram?.Name;
            name = string.IsNullOrEmpty(name) ? "Program" : string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            _path = Path.Combine(folder, name + " " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss") + ".png");
            _file = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            _png = new PngStreamWriter(_file, width, height);

            // A world-space canvas at the origin, one unit per pixel; the camera moves over it tile by tile.
            _captureRoot = new GameObject("VizzyStudioProgramImage");
            _captureRoot.layer = program.gameObject.layer;
            _captureRoot.transform.position = Origin;
            var canvas = _captureRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rootRect = (RectTransform)_captureRoot.transform;
            rootRect.sizeDelta = new Vector2(width, height);
            rootRect.pivot = new Vector2(0f, 1f);
            rootRect.position = Origin;

            var cameraObject = new GameObject("Camera");
            cameraObject.transform.SetParent(_captureRoot.transform, false);
            var camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.orthographicSize = TileHeight / 2f;
            camera.nearClipPlane = 1f;
            camera.farClipPlane = 1000f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = BackgroundColor(_controller);
            camera.cullingMask = 1 << program.gameObject.layer;
            _target = new RenderTexture(TileWidth, TileHeight, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            camera.targetTexture = _target;
            camera.aspect = TileWidth / (float)TileHeight;
            canvas.worldCamera = camera;
            _readBack = new Texture2D(TileWidth, TileHeight, TextureFormat.RGB24, false);

            _parent = program.parent;
            _siblingIndex = program.GetSiblingIndex();
            _localPosition = program.localPosition;
            _localScale = program.localScale;
            program.SetParent(_captureRoot.transform, false);
            _moved = true;
            program.localScale = new Vector3(scale, scale, 1f);
            program.localRotation = Quaternion.identity;

            // The box's top-left corner on the canvas's top-left corner (the root's pivot, at Origin).
            program.position = Origin;
            program.localPosition -= new Vector3(box.xMin * scale, box.yMax * scale, 0f);
            Canvas.ForceUpdateCanvases();
        }

        /// <summary>One tile into the band: the camera over it, rendered, read back, rows flipped to top-down RGB.</summary>
        private void RenderTile(int column, int row, int width, int rowsInBand, byte[] band)
        {
            var camera = _captureRoot.GetComponentInChildren<Camera>();
            var x0 = column * TileWidth;
            camera.transform.position = Origin + new Vector3(x0 + (TileWidth / 2f), -((row * TileHeight) + (TileHeight / 2f)), -100f);
            camera.Render();
            var active = RenderTexture.active;
            RenderTexture.active = _target;
            _readBack.ReadPixels(new Rect(0, 0, TileWidth, TileHeight), 0, 0, false);
            RenderTexture.active = active;
            _readBack.GetRawTextureData<byte>().CopyTo(_tile);
            var columns = Math.Min(TileWidth, width - x0);
            for (var y = 0; y < rowsInBand; y++)
            {
                // Texture rows go bottom-up.
                Buffer.BlockCopy(_tile, (TileHeight - 1 - y) * TileWidth * 3, band, ((y * width) + x0) * 3, columns * 3);
            }
        }

        private static Color BackgroundColor(VizzyUIController controller)
        {
            var image = controller.ProgramContainer == null ? null : controller.ProgramContainer.GetComponent<Image>();
            var color = image != null && image.color.a > 0.5f ? image.color : new Color(0.13f, 0.14f, 0.16f);
            color.a = 1f;
            return color;
        }

        private void ShowBlocker()
        {
            var canvas = _controller.ProgramContainer.GetComponentInParent<Canvas>();
            if (canvas == null)
            {
                return;
            }

            _blocker = new GameObject("VizzyStudioImageBlocker", typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)_blocker.transform;
            rect.SetParent(canvas.rootCanvas.transform, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.SetAsLastSibling();
            var image = _blocker.GetComponent<Image>();
            image.color = new Color(0f, 0f, 0f, 0.6f);
            image.raycastTarget = true;

            var textObject = new GameObject("Text", typeof(RectTransform));
            textObject.transform.SetParent(rect, false);
            var textRect = (RectTransform)textObject.transform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            _blockerText = textObject.AddComponent<TextMeshProUGUI>();
            var font = _controller.GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault(text => text.font != null);
            if (font != null)
            {
                _blockerText.font = font.font;
            }

            _blockerText.fontSize = 32;
            _blockerText.alignment = TextAlignmentOptions.Center;
            _blockerText.raycastTarget = false;
            this.SetProgress(0, 1);
        }

        private void SetProgress(int done, int total)
        {
            if (_blockerText != null)
            {
                _blockerText.text = Text.Get("VizzyStudio.Image.Saving", (100 * done / Math.Max(1, total)).ToString());
            }
        }

        private bool Try(string step, Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception e)
            {
                EditorLog.Error("Couldn't save the program image (" + step + "): " + e);
                this.End(true);
                if (_controller != null)
                {
                    _controller.ShowMessage(Text.Get("VizzyStudio.Image.Failed"), 5f);
                }

                return false;
            }
        }

        /// <summary>Everything back as it was; an unfinished file is deleted.</summary>
        private void End(bool failed)
        {
            if (_moved && _program != null && _parent != null)
            {
                _program.SetParent(_parent, false);
                _program.SetSiblingIndex(_siblingIndex);
                _program.localScale = _localScale;
                _program.localPosition = _localPosition;
            }

            _moved = false;
            Discard(ref _png);
            if (_file != null)
            {
                _file.Dispose();
                _file = null;
                if (failed && _path != null)
                {
                    try
                    {
                        File.Delete(_path);
                    }
                    catch (Exception e)
                    {
                        EditorLog.Warning("Couldn't delete the unfinished image " + _path + ": " + e.Message);
                    }
                }
            }

            if (_target != null)
            {
                _target.Release();
                Destroy(_target);
                _target = null;
            }

            if (_readBack != null)
            {
                Destroy(_readBack);
                _readBack = null;
            }

            if (_captureRoot != null)
            {
                Destroy(_captureRoot);
                _captureRoot = null;
            }

            if (_blocker != null)
            {
                Destroy(_blocker);
                _blocker = null;
            }

            if (_culling != null)
            {
                _culling.Paused = false;
                _culling = null;
            }

            _path = null;
            if (_running == this)
            {
                _running = null;
            }
        }

        private static void Discard(ref PngStreamWriter png)
        {
            png?.Dispose();
            png = null;
        }

        private void OnDisable()
        {
            if (_running == this)
            {
                this.StopAllCoroutines();
                this.End(true);
            }
        }

        private void OnDestroy()
        {
            if (_running == this)
            {
                this.End(true);
            }
        }
    }
}
