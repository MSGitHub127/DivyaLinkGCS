/**
 * airspace.js — DivyaLink GCS
 * ─────────────────────────────────────────────────────────────────────────────
 * Renders DGCA DigitalSky airspace zones on window._gcsMap (Leaflet).
 * Zones are transparent "glazes" — Mission Planner / QGroundControl aesthetic.
 * Purely informational: no enforcement, no blocking.
 *
 * DGCA DigitalSky GeoJSON format:
 *   Circular zones → { geometry: { type: "Point", coordinates: [lon, lat] },
 *                       properties: { radius: <metres>, type: "RED"|"YELLOW"|"GREEN", ... } }
 *   Polygon zones  → { geometry: { type: "Polygon", ... } }   (both are handled)
 *
 * Public API (window.airspace.*):
 *   loadZones(geoJsonString)   ← parse + render GeoJSON onto the map
 *   checkProximity(lat, lng)   ← proximity check, called per telemetry tick
 *   clearZones()               ← removes all zone layers
 *   setDotnetRef(ref)          ← wires up Blazor warning callback
 * ─────────────────────────────────────────────────────────────────────────────
 */
window.airspace = (() => {

    // ── State ────────────────────────────────────────────────────────────────
    let _zoneLayer = null;  // Active Leaflet GeoJSON layer
    let _dotnetRef = null;  // DotNetObjectReference for Blazor callbacks
    let _lastWarning = null;  // Last reported zone type ("RED"/"YELLOW"/null)

    // ── Zone styles (Mission Planner aesthetic) ──────────────────────────────
    const STYLES = {
        RED: {
            color: '#ff4d4d', fillColor: '#ff4d4d',
            fillOpacity: 0.15, weight: 1.5, opacity: 0.9
        },
        YELLOW: {
            color: '#ffcc00', fillColor: '#ffcc00',
            fillOpacity: 0.12, weight: 1.2, opacity: 0.85,
            dashArray: '6,4'
        },
        GREEN: {
            color: '#33ff33', fillColor: '#33ff33',
            fillOpacity: 0.08, weight: 1.0, opacity: 0.7,
            dashArray: '4,6'
        }
    };

    // ── Zone classification ──────────────────────────────────────────────────
    function classifyZone(props) {
        const raw = (props?.type || props?.zoneType || props?.category || '').toUpperCase();
        if (raw.includes('RED') || raw.includes('PROHIBITED') || raw.includes('RESTRICTED')) return 'RED';
        if (raw.includes('YELLOW') || raw.includes('CONTROLLED') || raw.includes('TMA') || raw.includes('CTR')) return 'YELLOW';
        return 'GREEN';
    }

    // ── Popup content (dark glass, subtle) ──────────────────────────────────
    function buildPopup(props) {
        const cls = classifyZone(props);
        const label = props?.name || props?.designator || props?.id || 'Airspace Zone';
        const floor = props?.lowerLimit ?? props?.lower_alt ?? '—';
        const ceiling = props?.upperLimit ?? props?.upper_alt ?? '—';
        const floorU = props?.lowerLimitUnit || 'AGL';
        const ceilU = props?.upperLimitUnit || 'AGL';
        const radius = props?.radius ? `${props.radius} m` : null;
        const reason = props?.reason || props?.remarks || '';

        const accent = cls === 'RED' ? '#ff4d4d' : cls === 'YELLOW' ? '#ffcc00' : '#33ff33';
        const accentBg = cls === 'RED'
            ? 'rgba(255,77,77,0.18)'
            : cls === 'YELLOW' ? 'rgba(255,204,0,0.16)' : 'rgba(51,255,51,0.12)';

        return `
<div style="font-family:'Rajdhani','Segoe UI',monospace;
            background:rgba(10,11,14,0.92);backdrop-filter:blur(10px);
            border:1px solid ${accent}55;border-radius:6px;
            padding:10px 14px;min-width:180px;max-width:240px;
            color:#e6edf3;font-size:0.78rem;
            box-shadow:0 4px 20px rgba(0,0,0,0.5);line-height:1.5;">
  <div style="display:inline-block;background:${accentBg};border:1px solid ${accent};
              color:${accent};border-radius:3px;padding:1px 7px;
              font-size:0.65rem;font-weight:800;letter-spacing:1.5px;
              text-transform:uppercase;margin-bottom:7px;">${cls} ZONE</div>
  <div style="font-weight:700;font-size:0.85rem;color:#fff;margin-bottom:5px;">${esc(label)}</div>
  <div style="color:#8b949e;font-size:0.72rem;margin-bottom:2px;">
    <span style="color:#58a6ff;">FLOOR</span>&nbsp;${esc(String(floor))} ${floorU}
  </div>
  <div style="color:#8b949e;font-size:0.72rem;">
    <span style="color:#58a6ff;">CEILING</span>&nbsp;${esc(String(ceiling))} ${ceilU}
  </div>
  ${radius ? `<div style="color:#8b949e;font-size:0.72rem;margin-top:2px;"><span style="color:#58a6ff;">RADIUS</span>&nbsp;${radius}</div>` : ''}
  ${reason ? `<div style="color:#6e7681;font-size:0.68rem;margin-top:4px;border-top:1px solid #30363d;padding-top:4px;">${esc(reason)}</div>` : ''}
</div>`;
    }

    function esc(s) {
        return String(s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;')
            .replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    // ── Map guard ────────────────────────────────────────────────────────────
    function getMap() {
        return (window._gcsMap && typeof window._gcsMap.getBounds === 'function')
            ? window._gcsMap : null;
    }

    // ── loadZones ────────────────────────────────────────────────────────────
    /**
     * Parses a GeoJSON FeatureCollection string and renders all zones.
     *
     * Handles two DGCA geometry types:
     *   Point + radius property  → L.circle   (official DGCA circular zone)
     *   Polygon / MultiPolygon   → L.polygon  (complex boundary shapes)
     */
    function loadZones(geoJsonString) {
        const map = getMap();
        if (!map) { console.warn('[airspace] loadZones: map not ready.'); return; }

        clearZones();

        let data;
        try {
            data = typeof geoJsonString === 'string'
                ? JSON.parse(geoJsonString) : geoJsonString;
        } catch (e) {
            console.error('[airspace] Invalid GeoJSON:', e);
            return;
        }

        if (!data?.features?.length) {
            console.info('[airspace] No airspace features in response.');
            return;
        }

        _zoneLayer = L.geoJSON(data, {

            // ── pointToLayer: convert DGCA circular zones to L.circle ────────
            // DGCA DigitalSky represents circular airspace as a GeoJSON Point
            // with a `radius` field (in metres) in the feature properties.
            // Leaflet's default would render this as a pin marker — wrong.
            // We intercept it here and create a proper L.circle instead.
            pointToLayer(feature, latlng) {
                const props = feature.properties || {};
                const cls = classifyZone(props);
                const style = STYLES[cls] || STYLES.GREEN;
                const radius = props.radius || props.Radius || props.radiusM || 500; // metres

                return L.circle(latlng, {
                    radius,
                    ...style
                });
            },

            // ── style: applies to Polygon / MultiPolygon features ────────────
            style(feature) {
                const cls = classifyZone(feature.properties);
                return STYLES[cls] || STYLES.GREEN;
            },

            // ── onEachFeature: hover highlight + click popup ─────────────────
            onEachFeature(feature, layer) {
                const props = feature.properties || {};
                const cls = classifyZone(props);
                const base = STYLES[cls] || STYLES.GREEN;

                layer.on('mouseover', function () {
                    this.setStyle({
                        fillOpacity: Math.min(base.fillOpacity * 3, 0.45),
                        weight: (base.weight || 1) + 1
                    });
                });
                layer.on('mouseout', function () {
                    _zoneLayer.resetStyle(this);
                });
                layer.on('click', function (e) {
                    L.popup({
                        className: 'dgca-popup',
                        maxWidth: 260,
                        closeButton: true,
                        autoClose: true,
                        closeOnClick: false,
                        offset: [0, -4]
                    })
                        .setLatLng(e.latlng)
                        .setContent(buildPopup(props))
                        .openOn(map);
                });
            }
        }).addTo(map);

        console.info(`[airspace] Rendered ${data.features.length} zone(s).`);
    }

    // ── checkProximity ───────────────────────────────────────────────────────
    // Called per telemetry tick from MainLayout (throttled to ~2 s).
    // Supports both L.circle (DGCA) and L.Polygon geometries.
    function checkProximity(lat, lng) {
        if (!_zoneLayer || !_dotnetRef) return;

        const dronePoint = L.latLng(lat, lng);
        let detected = null;

        _zoneLayer.eachLayer(layer => {
            if (detected === 'RED') return;
            const cls = classifyZone(layer.feature?.properties || {});
            if (cls === 'GREEN') return;
            if (isPointInLayer(dronePoint, layer)) {
                if (cls === 'RED' || detected === null) detected = cls;
            }
        });

        if (detected !== _lastWarning) {
            _lastWarning = detected;
            try { _dotnetRef.invokeMethodAsync('UpdateAirspaceWarning', detected); }
            catch (e) { console.warn('[airspace] Blazor callback failed:', e); }
        }
    }

    // ── Geometry helpers ─────────────────────────────────────────────────────

    function isPointInLayer(latLng, layer) {
        try {
            // L.Circle: use Leaflet's built-in distance check
            if (layer instanceof L.Circle) {
                return layer.getLatLng().distanceTo(latLng) <= layer.getRadius();
            }
            // L.Polygon / L.Polyline: bounding box pre-check, then ray-cast
            if (layer instanceof L.Polygon) {
                if (!layer.getBounds().contains(latLng)) return false;
                return testRings(layer.getLatLngs(), latLng);
            }
        } catch { /* malformed geometry */ }
        return false;
    }

    function testRings(arr, point) {
        if (!Array.isArray(arr)) return false;
        if (arr.length && arr[0] instanceof L.LatLng) return raycast(arr, point);
        let inOuter = false;
        for (let i = 0; i < arr.length; i++) {
            const sub = arr[i];
            if (Array.isArray(sub) && sub.length && sub[0] instanceof L.LatLng) {
                const hit = raycast(sub, point);
                if (i === 0) inOuter = hit;
                else if (hit) return false; // inside a hole
            } else {
                if (testRings(sub, point)) return true;
            }
        }
        return inOuter;
    }

    function raycast(ring, point) {
        let inside = false;
        const x = point.lat, y = point.lng;
        for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
            const xi = ring[i].lat, yi = ring[i].lng;
            const xj = ring[j].lat, yj = ring[j].lng;
            if (((yi > y) !== (yj > y)) && (x < (xj - xi) * (y - yi) / (yj - yi) + xi))
                inside = !inside;
        }
        return inside;
    }

    // ── clearZones ───────────────────────────────────────────────────────────
    function clearZones() {
        const map = getMap();
        if (_zoneLayer && map) { map.removeLayer(_zoneLayer); _zoneLayer = null; }
        _lastWarning = null;
    }

    // ── setDotnetRef ─────────────────────────────────────────────────────────
    function setDotnetRef(ref) { _dotnetRef = ref; }

    // ── attachMoveListener ───────────────────────────────────────────────────
    // Called once from initMap() after window._gcsMap is created.
    // Also called by the flightplan.js IIFE waiter — the _attached guard
    // ensures the listener is only registered once regardless of which
    // caller gets there first.
    let _attached = false;
    let _debounceTimer = null;

    function attachMoveListener() {
        if (_attached) return;
        const map = getMap();
        if (!map) { console.warn('[airspace] attachMoveListener: map not ready.'); return; }
        _attached = true;

        map.on('moveend', () => {
            if (_debounceTimer) clearTimeout(_debounceTimer);
            _debounceTimer = setTimeout(async () => {
                const b = map.getBounds();
                const url = `/api/airspace?minLat=${b.getSouth()}&minLon=${b.getWest()}&maxLat=${b.getNorth()}&maxLon=${b.getEast()}`;
                try {
                    const res = await fetch(url);
                    if (!res.ok) { console.warn(`[airspace] API ${res.status}`); return; }
                    loadZones(await res.text());
                } catch (err) {
                    console.error('[airspace] Fetch error:', err);
                }
            }, 500);
        });

        // Fire immediately for the current viewport — no pan needed to see zones
        map.fire('moveend');
        console.info('[airspace] moveend listener attached.');
    }

    return { loadZones, checkProximity, clearZones, setDotnetRef, attachMoveListener };

})();