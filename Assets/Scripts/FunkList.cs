namespace Assets.Scripts
{
    using System;
    using System.Collections.Generic;
    using TMPro;
    using UnityEngine;
    using UnityEngine.EventSystems;
    using UnityEngine.UI;

    /// <summary>The list under a Funk field. A row's press accepts it and its hover selects it.</summary>
    public sealed class FunkList
    {
        private const int VisibleRows = 8;
        private const float RowHeight = 26f;
        private const float Width = 420f;

        private static readonly Color Background = new Color(0.09f, 0.12f, 0.17f, 0.98f);
        private static readonly Color Selected = new Color(0.16f, 0.42f, 0.72f, 1f);
        private static readonly Color Clear = new Color(0f, 0f, 0f, 0f);

        private readonly GameObject _root;
        private readonly RectTransform _rect;
        private readonly List<Image> _rowImages = new List<Image>();
        private readonly List<TextMeshProUGUI> _rowTexts = new List<TextMeshProUGUI>();
        private int _first;

        public FunkList(TMP_InputField field, Action<int> onPressed, Action<int> onHovered)
        {
            var rootCanvas = field.GetComponentInParent<Canvas>().rootCanvas;
            _root = new GameObject("VizzyStudio.FunkList", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster), typeof(Image));
            _rect = (RectTransform)_root.transform;
            _rect.SetParent(rootCanvas.transform, false);
            _rect.anchorMin = _rect.anchorMax = new Vector2(0.5f, 0.5f);
            _rect.pivot = new Vector2(0f, 1f);
            _rect.localScale = Vector3.one;

            var canvas = _root.GetComponent<Canvas>();
            canvas.overrideSorting = true;
            canvas.sortingOrder = 30000;

            var background = _root.GetComponent<Image>();
            background.color = Background;
            background.raycastTarget = false;

            for (var i = 0; i < VisibleRows; i++)
            {
                var row = new GameObject("Row", typeof(RectTransform), typeof(Image));
                var rowRect = (RectTransform)row.transform;
                rowRect.SetParent(_rect, false);
                rowRect.anchorMin = new Vector2(0f, 1f);
                rowRect.anchorMax = new Vector2(1f, 1f);
                rowRect.pivot = new Vector2(0.5f, 1f);
                rowRect.sizeDelta = new Vector2(0f, RowHeight);
                rowRect.anchoredPosition = new Vector2(0f, -i * RowHeight);
                var image = row.GetComponent<Image>();
                var rowIndex = i;
                var input = row.AddComponent<FunkListRow>();
                input.Pressed = () => onPressed(_first + rowIndex);
                input.Hovered = () => onHovered(_first + rowIndex);
                _rowImages.Add(image);

                var textObject = new GameObject("Text", typeof(RectTransform));
                var textRect = (RectTransform)textObject.transform;
                textRect.SetParent(rowRect, false);
                textRect.anchorMin = Vector2.zero;
                textRect.anchorMax = Vector2.one;
                textRect.offsetMin = new Vector2(8f, 0f);
                textRect.offsetMax = new Vector2(-8f, 0f);
                var text = textObject.AddComponent<TextMeshProUGUI>();
                text.font = field.textComponent.font;
                text.fontSize = 16f;
                text.color = Color.white;
                text.alignment = TextAlignmentOptions.MidlineLeft;
                text.enableWordWrapping = false;
                text.overflowMode = TextOverflowModes.Ellipsis;
                text.raycastTarget = false;
                _rowTexts.Add(text);
            }
        }

        public void Show(TMP_InputField field, IList<FunkSuggestion> suggestions, int selected, int wordStart)
        {
            var count = Mathf.Min(VisibleRows, suggestions.Count);

            // Scrolls only when the selection leaves the view, so rows don't move under the mouse.
            var first = Mathf.Clamp(_first, 0, suggestions.Count - count);
            first = selected < first ? selected : selected >= first + count ? selected - count + 1 : first;
            _first = first;
            for (var i = 0; i < VisibleRows; i++)
            {
                var visible = i < count;
                _rowImages[i].gameObject.SetActive(visible);
                if (visible)
                {
                    _rowTexts[i].text = suggestions[first + i].Display;
                    _rowImages[i].color = first + i == selected ? Selected : Clear;
                }
            }

            var height = count * RowHeight;
            _rect.sizeDelta = new Vector2(Width, height);
            Place(field, height, wordStart);
            _root.SetActive(true);
        }

        public void Hide()
        {
            _root.SetActive(false);
        }

        public void Destroy()
        {
            UnityEngine.Object.Destroy(_root);
        }

        /// <summary>Under the word being typed, kept on screen; above the field when there's no room below.</summary>
        private void Place(TMP_InputField field, float height, int wordStart)
        {
            var corners = new Vector3[4];
            ((RectTransform)field.transform).GetWorldCorners(corners);
            var parent = (RectTransform)_rect.parent;
            var bottomLeft = parent.InverseTransformPoint(corners[0]);
            var topLeft = parent.InverseTransformPoint(corners[1]);
            var bounds = parent.rect;

            var y = bottomLeft.y - height < bounds.yMin ? topLeft.y + height : bottomLeft.y;
            var x = Mathf.Max(bottomLeft.x, WordLeft(field, wordStart, parent, bottomLeft.x));
            x = Mathf.Max(bounds.xMin, Mathf.Min(x, bounds.xMax - Width));
            _rect.localPosition = new Vector3(x, y, 0f);
        }

        /// <summary>Where the word's first letter is, in the list's parent; the fallback if the text isn't laid out.</summary>
        private static float WordLeft(TMP_InputField field, int wordStart, RectTransform parent, float fallback)
        {
            var text = field.textComponent;
            var info = text.textInfo;
            if (info == null || info.characterCount == 0 || wordStart < 0 || wordStart >= info.characterCount)
            {
                return fallback;
            }

            var world = text.transform.TransformPoint(new Vector3(info.characterInfo[wordStart].origin, 0f, 0f));
            return parent.InverseTransformPoint(world).x;
        }

        private sealed class FunkListRow : MonoBehaviour, IPointerDownHandler, IPointerEnterHandler
        {
            public Action Pressed { get; set; }

            public Action Hovered { get; set; }

            public void OnPointerDown(PointerEventData eventData) => Pressed?.Invoke();

            public void OnPointerEnter(PointerEventData eventData) => Hovered?.Invoke();
        }
    }
}
