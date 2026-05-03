using Mafi;
using Mafi.Unity;
using UnityEngine;

namespace PlaceResourceMod;

/// <summary>
/// Draws a horizontal preview circle in world space at the cursor's terrain position.
/// 32-segment LineRenderer; world units (Mafi tile = 0.5 Unity units after the *2 scale
/// in Tile3f.ToVector3, so 1 tile radius = 2 Unity units).
/// </summary>
public sealed class PreviewCircleMb : MonoBehaviour {

	private const int SEGMENTS = 32;
	private const float MAFI_TILE_TO_UNITY = 2f;

	private LineRenderer m_lr;
	private Tile3f m_center;
	private int m_radius = 1;
	private Color m_color = Color.white;
	private bool m_dirty = true;

	private void Awake() {
		m_lr = gameObject.AddComponent<LineRenderer>();
		m_lr.useWorldSpace = true;
		m_lr.loop = true;
		m_lr.positionCount = SEGMENTS;
		m_lr.startWidth = 0.4f;
		m_lr.endWidth = 0.4f;
		m_lr.material = new Material(Shader.Find("Sprites/Default"));
	}

	public void SetCenter(Tile3f center) {
		m_center = center;
		m_dirty = true;
	}

	public void SetRadius(int radiusTiles) {
		m_radius = radiusTiles;
		m_dirty = true;
	}

	public void SetColor(Color c) {
		m_color = c;
		if (m_lr != null) {
			m_lr.startColor = c;
			m_lr.endColor = c;
		}
	}

	public void SetVisible(bool visible) {
		if (m_lr != null) m_lr.enabled = visible;
	}

	private void LateUpdate() {
		if (!m_dirty || m_lr == null) return;
		m_dirty = false;

		Vector3 c = m_center.ToVector3();
		float r = m_radius * MAFI_TILE_TO_UNITY;
		for (int i = 0; i < SEGMENTS; i++) {
			float angle = (i / (float)SEGMENTS) * 2f * Mathf.PI;
			m_lr.SetPosition(i, new Vector3(c.x + Mathf.Cos(angle) * r, c.y + 0.5f, c.z + Mathf.Sin(angle) * r));
		}
	}
}
