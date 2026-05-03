using Mafi.Unity.UiStatic;
using UnityEngine;

namespace PlaceResourceMod;

/// <summary>
/// Floating IMGUI panel anchored near the cursor. Renders a stack of arbitrary text lines.
/// Respects COI's UI scale setting via UiScaleHelper.GetCurrentScaleFloat() — sets GUI.matrix
/// so all sizes scale uniformly without per-element multiplication.
/// </summary>
public sealed class InfoPanelMb : MonoBehaviour {

	private const float PANEL_WIDTH = 220f;
	private const float CURSOR_OFFSET = 18f;
	private const float V_PADDING = 8f;
	private const float H_PADDING = 8f;

	private string[] m_lines = System.Array.Empty<string>();
	private GUIStyle m_style;
	private GUIStyle m_bgStyle;
	private float m_lineHeight = -1f;

	public void SetLines(params string[] lines) {
		m_lines = lines ?? System.Array.Empty<string>();
	}

	private void OnGUI() {
		if (m_lines.Length == 0) return;
		if (m_style == null) {
			m_style = new GUIStyle(GUI.skin.label) {
				fontSize = 14,
				normal = { textColor = Color.white },
				margin = new RectOffset(0, 0, 0, 0),
				padding = new RectOffset(0, 0, 0, 0),
			};
			m_bgStyle = new GUIStyle(GUI.skin.box);
			// Measure once. With margin/padding zeroed, each GUILayout.Label takes exactly
			// CalcSize().y so total height = N * lineHeight + V_PADDING * 2.
			m_lineHeight = m_style.CalcSize(new GUIContent("Mg")).y;
		}

		float scale = UiScaleHelper.GetCurrentScaleFloat();
		GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));

		// IMGUI Y origin is top-left, Input.mousePosition is bottom-left. ApplyScale converts
		// screen pixels into the post-matrix GUI space.
		Vector2 mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y).ApplyScale();
		float h = m_lines.Length * m_lineHeight + V_PADDING * 2f;
		Rect r = new Rect(mouse.x + CURSOR_OFFSET, mouse.y + CURSOR_OFFSET, PANEL_WIDTH, h);

		GUI.Box(r, GUIContent.none, m_bgStyle);
		// Direct GUI.Label per line with explicit Rect, no GUILayout. Each line occupies exactly
		// m_lineHeight pixels starting V_PADDING from the top of the box, indented H_PADDING from
		// the left. All px values are in pre-matrix GUI space, so they scale with UI scale.
		for (int i = 0; i < m_lines.Length; i++) {
			Rect lineRect = new Rect(
				r.x + H_PADDING,
				r.y + V_PADDING + i * m_lineHeight,
				r.width - H_PADDING * 2f,
				m_lineHeight);
			GUI.Label(lineRect, m_lines[i], m_style);
		}
	}
}
