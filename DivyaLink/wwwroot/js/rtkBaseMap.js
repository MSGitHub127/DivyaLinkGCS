/**
 * rtkBaseMap.js
 * Leaflet satellite map for the RTK Base Station Fixed Coords tab.
 *
 * Shows:
 *  • Esri World Imagery satellite tiles (same visual as Mission Planner)
 *  • Esri label overlay (road/place names)
 *  • Precision crosshair marker at the surveyed base position
 *  • Accuracy circle (radius = survey accuracy in metres)
 *  • Coordinate readout in a floating card
 *
 * Public API (called from Blazor via JS interop):
 *   rtkBaseMap.init(containerId, lat, lon, accuracyM, alt)
 *   rtkBaseMap.update(lat, lon, accuracyM, alt)
 *   rtkBaseMap.destroy()
 *
 * Leaflet and its CSS must already be loaded (they are, via airspace.js).
 */
window.rtkBaseMap = (() => {

    // ── Internal state ────────────────────────────────────────────────────
    let _map = null;
    let _marker = null;
    let _circle = null;
    let _infoCard = null;
    let _containerId = null;

    // ── Tile layers ───────────────────────────────────────────────────────
    // Esri World Imagery — satellite, no API key required.
    // Same imagery source Mission Planner uses in its map view.
    const ESRI_SAT_URL = 'https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}';
    const ESRI_SAT_ATTR = 'Tiles &copy; Esri &mdash; Source: Esri, USGS, AEX, GeoEye, IGN';

    // Esri label overlay — renders road/place names over satellite imagery
    const ESRI_LABEL_URL = 'https://services.arcgisonline.com/ArcGIS/rest/services/Reference/World_Boundaries_and_Places/MapServer/tile/{z}/{y}/{x}';
    const ESRI_LABEL_ATTR = 'Labels &copy; Esri';

    // ── Crosshair marker icon (SVG, precision survey reticle) ─────────────
    function makeCrosshairIcon() {
        const svg = `
<svg xmlns="http://www.w3.org/2000/svg" width="40" height="40" viewBox="0 0 40 40">
  <!-- Outer ring -->
  <circle cx="20" cy="20" r="14" fill="none" stroke="#00f2ff" stroke-width="1.5" opacity="0.9"/>
  <!-- Inner dot -->
  <circle cx="20" cy="20" r="3" fill="#00f2ff" opacity="1"/>
  <!-- Cross arms -->
  <line x1="20" y1="2"  x2="20" y2="11" stroke="#00f2ff" stroke-width="1.5" stroke-linecap="round"/>
  <line x1="20" y1="29" x2="20" y2="38" stroke="#00f2ff" stroke-width="1.5" stroke-linecap="round"/>
  <line x1="2"  y1="20" x2="11" y2="20" stroke="#00f2ff" stroke-width="1.5" stroke-linecap="round"/>
  <line x1="29" y1="20" x2="38" y2="20" stroke="#00f2ff" stroke-width="1.5" stroke-linecap="round"/>
  <!-- Corner ticks (45°) -->
  <line x1="9"  y1="9"  x2="14" y2="14" stroke="#00f2ff" stroke-width="1"   stroke-linecap="round" opacity="0.6"/>
  <line x1="31" y1="9"  x2="26" y2="14" stroke="#00f2ff" stroke-width="1"   stroke-linecap="round" opacity="0.6"/>
  <line x1="9"  y1="31" x2="14" y2="26" stroke="#00f2ff" stroke-width="1"   stroke-linecap="round" opacity="0.6"/>
  <line x1="31" y1="31" x2="26" y2="26" stroke="#00f2ff" stroke-width="1"   stroke-linecap="round" opacity="0.6"/>
</svg>`;
        return L.divIcon({
            html: svg,
            className: '',
            iconSize: [40, 40],
            iconAnchor: [20, 20],
            popupAnchor: [0, -24]
        });
    }

    // ── Coordinate info card (floating HTML overlay) ──────────────────────
    function buildInfoCard(lat, lon, alt, accuracyM) {
        const accText = accuracyM < 9000 ? `${accuracyM.toFixed(3)} m` : '—';
        const altText = alt !== null && alt !== undefined ? `${alt.toFixed(3)} m` : '—';
        return `
<div style="
    position:absolute; bottom:12px; left:12px; z-index:500;
    background:rgba(13,17,23,0.88); backdrop-filter:blur(8px);
    border:1px solid rgba(0,242,255,0.25); border-radius:6px;
    padding:8px 12px; font-family:monospace; font-size:0.72rem;
    color:#c9d1d9; min-width:220px; pointer-events:none;
    box-shadow:0 4px 24px rgba(0,0,0,0.5);
">
  <div style="color:#00f2ff;font-weight:700;letter-spacing:1px;font-size:0.65rem;margin-bottom:5px;font-family:sans-serif">
    ◉ BASE STATION POSITION
  </div>
  <div style="display:flex;flex-direction:column;gap:3px;">
    <div><span style="color:#8b949e;min-width:32px;display:inline-block">LAT</span>
         <span style="color:#fff;font-weight:700">${lat.toFixed(8)}°</span></div>
    <div><span style="color:#8b949e;min-width:32px;display:inline-block">LON</span>
         <span style="color:#fff;font-weight:700">${lon.toFixed(8)}°</span></div>
    <div><span style="color:#8b949e;min-width:32px;display:inline-block">ALT</span>
         <span style="color:#fff;font-weight:700">${altText}</span></div>
    <div style="margin-top:3px;padding-top:3px;border-top:1px solid rgba(255,255,255,0.1)">
         <span style="color:#8b949e;min-width:32px;display:inline-block">ACC</span>
         <span style="color:#3fb950;font-weight:700">${accText}</span>
    </div>
  </div>
</div>`;
    }

    // ── Zoom level from accuracy ──────────────────────────────────────────
    // Maps survey accuracy to a sensible satellite zoom level.
    // At zoom 20 (~0.15m/px at equator), even 0.2m accuracy is just 1-2px wide.
    // We use zoom 19 for sub-metre accuracy so the area context is visible.
    function zoomForAccuracy(accuracyM) {
        if (accuracyM < 1) return 19;
        if (accuracyM < 5) return 18;
        if (accuracyM < 15) return 17;
        if (accuracyM < 50) return 16;
        if (accuracyM < 150) return 15;
        if (accuracyM < 500) return 14;
        return 13;
    }

    // ── Public: init ──────────────────────────────────────────────────────
    function init(containerId, lat, lon, accuracyM, alt) {
        // If already initialised on the same container, just update
        if (_map && _containerId === containerId) {
            update(lat, lon, accuracyM, alt);
            return;
        }

        destroy(); // clean up any previous instance

        const el = document.getElementById(containerId);
        if (!el) { console.warn('[rtkBaseMap] container not found:', containerId); return; }

        _containerId = containerId;

        // Create map — disable default attribution (we add our own)
        _map = L.map(el, {
            center: [lat, lon],
            zoom: zoomForAccuracy(accuracyM),
            zoomControl: true,
            attributionControl: false,
            scrollWheelZoom: true,
        });

        // Satellite tiles
        L.tileLayer(ESRI_SAT_URL, {
            maxZoom: 20,
            maxNativeZoom: 19,
            attribution: ESRI_SAT_ATTR
        }).addTo(_map);

        // Label overlay
        L.tileLayer(ESRI_LABEL_URL, {
            maxZoom: 20,
            maxNativeZoom: 19,
            attribution: ESRI_LABEL_ATTR,
            opacity: 0.8
        }).addTo(_map);

        // Accuracy circle
        _circle = L.circle([lat, lon], {
            radius: Math.max(accuracyM, 0.5),   // min visible radius 0.5m
            color: '#00f2ff',
            weight: 1.5,
            opacity: 0.9,
            fillColor: '#00f2ff',
            fillOpacity: 0.12,
            interactive: false
        }).addTo(_map);

        // Precision crosshair marker
        _marker = L.marker([lat, lon], {
            icon: makeCrosshairIcon(),
            interactive: false,
            zIndexOffset: 1000
        }).addTo(_map);

        // Floating coordinate card — injected as a raw DOM element
        _infoCard = document.createElement('div');
        _infoCard.innerHTML = buildInfoCard(lat, lon, alt, accuracyM);
        el.style.position = 'relative';
        el.appendChild(_infoCard);

        // Force Leaflet to recalculate layout (needed in hidden/flex containers)
        setTimeout(() => { if (_map) _map.invalidateSize(); }, 120);
    }

    // ── Public: update ────────────────────────────────────────────────────
    function update(lat, lon, accuracyM, alt) {
        if (!_map) return;

        const latlng = L.latLng(lat, lon);

        _marker.setLatLng(latlng);
        _circle.setLatLng(latlng);
        _circle.setRadius(Math.max(accuracyM, 0.5));

        // Pan map to new position (smooth animation)
        _map.panTo(latlng, { animate: true, duration: 0.5 });

        // Update zoom if accuracy changed significantly
        const idealZoom = zoomForAccuracy(accuracyM);
        if (Math.abs(_map.getZoom() - idealZoom) > 1) {
            _map.setZoom(idealZoom);
        }

        // Refresh info card
        if (_infoCard) {
            _infoCard.innerHTML = buildInfoCard(lat, lon, alt, accuracyM);
        }
    }

    // ── Public: destroy ───────────────────────────────────────────────────
    function destroy() {
        if (_map) {
            _map.remove();
            _map = null;
            _marker = null;
            _circle = null;
        }
        if (_infoCard) {
            _infoCard.remove();
            _infoCard = null;
        }
        _containerId = null;
    }

    return { init, update, destroy };
})();