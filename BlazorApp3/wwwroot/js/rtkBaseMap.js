/**
 * rtkBaseMap.js
 * Place in: wwwroot/js/rtkBaseMap.js
 *
 * Leaflet map bridge for the RTK "Fixed Coords" tab (Tab 1).
 * Called from RtkDrawer.razor via OnAfterRenderAsync:
 *
 * rtkBaseMap.init(containerId, lat, lng, accuracyM, altM)
 * rtkBaseMap.update(lat, lng, accuracyM, altM)
 * rtkBaseMap.destroy()
 */
window.rtkBaseMap = (() => {
    'use strict';

    // ── Esri World Imagery Satellite & Labels Tile Sources ──
    const SAT_URL = 'https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}';
    const SAT_ATTR = 'Tiles &copy; Esri &mdash; Source: Esri, i-cubed, USDA, USGS, AEX, GeoEye, Getmapping, Aerogrid, IGN, IGP, UPR-EGP, and the GIS User Community';

    const LBL_URL = 'https://services.arcgisonline.com/ArcGIS/rest/services/Reference/World_Boundaries_and_Places/MapServer/tile/{z}/{y}/{x}';

    /** Fixed initial zoom level — never overridden by this module. */
    const FIXED_ZOOM = 16;

    /** Accuracy threshold in metres below which the halo turns green. */
    const ACCURACY_LOCK_M = 1.0;

    /** Halo colour when accuracy > ACCURACY_LOCK_M (nominal blue). */
    const COLOR_NOMINAL = '#58a6ff';

    /** Halo colour when accuracy ≤ ACCURACY_LOCK_M (secure baseline green). */
    const COLOR_LOCKED = '#3fb950';

    /** Minimum circle radius in metres. */
    const MIN_CIRCLE_R = 0.3;

    /** panTo animation duration in seconds. */
    const PAN_DURATION_S = 0.35;

    // ── Module state ──────────────────────────────────────────────────────────

    let _map = null;            // Leaflet Map instance
    let _marker = null;         // L.Marker  — position reticle
    let _circle = null;         // L.Circle  — accuracy halo
    let _badge = null;          // HTMLElement — data overlay badge
    let _containerId = null;    // string — id of the active host <div>
    let _resizeObserver = null; // ✨ Observes dimension shifts for auto-sizing

    // ── Private helpers ───────────────────────────────────────────────────────

    function _accentColor(accM) {
        return accM <= ACCURACY_LOCK_M ? COLOR_LOCKED : COLOR_NOMINAL;
    }

    function _makeIcon() {
        return L.divIcon({
            className: '',
            html: `<svg width="20" height="20" viewBox="0 0 20 20"
                        xmlns="http://www.w3.org/2000/svg">
                     <circle cx="10" cy="10" r="5"
                             fill="#58a6ff" stroke="white" stroke-width="2.5"/>
                   </svg>`,
            iconSize: [20, 20],
            iconAnchor: [10, 10],
        });
    }

    function _buildBadge(containerEl, accM, altM) {
        let badge = document.getElementById('rtk-base-badge');

        if (!badge) {
            badge = document.createElement('div');
            badge.id = 'rtk-base-badge';
            Object.assign(badge.style, {
                position: 'absolute',
                bottom: '8px',
                left: '8px',
                zIndex: '1000',
                background: 'rgba(13,17,23,0.88)',
                border: '1px solid rgba(88,166,255,0.30)',
                borderRadius: '6px',
                padding: '4px 9px',
                fontFamily: "'JetBrains Mono','Courier New',monospace",
                fontSize: '11px',
                color: '#c9d1d9',
                lineHeight: '1.6',
                pointerEvents: 'none',
                userSelect: 'none',
            });
            containerEl.style.position = 'relative';
            containerEl.appendChild(badge);
        }

        const color = _accentColor(accM);
        const locked = accM <= ACCURACY_LOCK_M;

        badge.innerHTML =
            `<span style="color:#8b949e">acc\u00a0</span>` +
            `<b style="color:${color}">${accM.toFixed(3)}\u00a0m</b>` +
            (locked
                ? `\u00a0<span style="color:${COLOR_LOCKED};font-size:9px;` +
                `font-weight:700">\u25c6\u00a0LOCKED</span>`
                : '') +
            `\u00a0\u00a0<span style="color:#8b949e">alt\u00a0</span>` +
            `<b style="color:#c9d1d9">${altM.toFixed(1)}\u00a0m</b>`;

        return badge;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    function init(containerId, lat, lng, accuracyM, altM) {
        if (_map && _containerId === containerId) {
            update(lat, lng, accuracyM, altM);
            return;
        }

        destroy();

        const el = document.getElementById(containerId);
        if (!el) {
            console.warn('[rtkBaseMap] container not found:', containerId);
            return;
        }
        _containerId = containerId;

        // ── Create Leaflet map ────────────────────────────────────────────────
        _map = L.map(el, {
            center: [lat, lng],
            zoom: FIXED_ZOOM,
            zoomControl: true,
            attributionControl: false,
            scrollWheelZoom: true,
            doubleClickZoom: true,
            touchZoom: true,
            dragging: true,
        });

        // ── 1. Primary High-Resolution Satellite Layer ────────────────────────
        L.tileLayer(SAT_URL, {
            maxZoom: 19,
            maxNativeZoom: 19,
            attribution: SAT_ATTR,
        }).addTo(_map);

        // ── 2. Secondary Textual Boundaries Overlay Layer ─────────────────────
        L.tileLayer(LBL_URL, {
            maxZoom: 19,
            maxNativeZoom: 19,
            opacity: 0.85
        }).addTo(_map);

        // ── Accuracy halo (circle) ────────────────────────────────────────────
        const color = _accentColor(accuracyM);
        _circle = L.circle([lat, lng], {
            radius: Math.max(accuracyM, MIN_CIRCLE_R),
            color: color,
            weight: 1.5,
            opacity: 0.85,
            fillColor: color,
            fillOpacity: 0.12,
            interactive: false,
        }).addTo(_map);

        // ── Position reticle (marker) ─────────────────────────────────────────
        _marker = L.marker([lat, lng], {
            icon: _makeIcon(),
            interactive: false,
            zIndexOffset: 1000,
        }).addTo(_map);

        // ── Data overlay badge ────────────────────────────────────────────────
        _badge = _buildBadge(el, accuracyM, altM);

        // ── ✨ AUTO-SIZE COUPLER ENGINE ───────────────────────────────────────
        // Attaches a layout loop sensor. If the width/height shifts by even 1px, 
        // forces an instant internal layout boundary refresh to prevent tile clipping.
        if (window.ResizeObserver) {
            _resizeObserver = new ResizeObserver(() => {
                if (_map) {
                    _map.invalidateSize({ animate: false });
                }
            });
            _resizeObserver.observe(el);
        }

        // Quick fallback safety trigger
        setTimeout(() => { if (_map) _map.invalidateSize(); }, 50);
    }

    function update(lat, lng, accuracyM, altM) {
        if (!_map) return;

        const ll = L.latLng(lat, lng);
        const color = _accentColor(accuracyM);

        _marker.setLatLng(ll);

        _circle.setLatLng(ll);
        _circle.setRadius(Math.max(accuracyM, MIN_CIRCLE_R));
        _circle.setStyle({
            color: color,
            fillColor: color,
        });

        _map.panTo(ll, { animate: true, duration: PAN_DURATION_S });

        const el = document.getElementById(_containerId);
        if (el) _buildBadge(el, accuracyM, altM);
    }

    function destroy() {
        // Disconnect layout observers safely to prevent memory leak trails
        if (_resizeObserver) {
            _resizeObserver.disconnect();
            _resizeObserver = null;
        }
        if (_badge) { _badge.remove(); _badge = null; }
        if (_map) {
            _map.remove();
            _map = null;
            _marker = null;
            _circle = null;
        }
        _containerId = null;
    }

    return { init, update, destroy };
})();