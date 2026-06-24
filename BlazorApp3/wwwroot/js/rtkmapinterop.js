/**
 * rtkMapInterop.js
 * ─────────────────────────────────────────────────────────────────────────────
 * Lightweight Leaflet map bridge for the RTK Base Station mini-map.
 * Provides three window-level entry points called from Blazor JS interop:
 *
 *   window.rtkMapInterop.init(containerId, lat, lng, accuracyM, alt, phase)
 *   window.rtkMapInterop.update(lat, lng, accuracyM, alt, phase)
 *   window.rtkMapInterop.destroy()
 *
 * Design principles
 * ─────────────────
 * • Completely isolated from the primary mission map — uses its own L.map()
 *   instance stored in module-level variables that are never accessible to
 *   the flight canvas layer.
 * • Idempotent init: calling init() a second time on the same container
 *   updates the view rather than creating a duplicate instance.
 * • Zero flicker on updates: marker and circle are mutated in place;
 *   the tile layer is never rebuilt after initialisation.
 * • Dark-first tile strategy: Esri World Imagery (satellite) primary tile,
 *   Esri label overlay on top — matches DivyaLink's dark aesthetic and
 *   mirrors the tile source Mission Planner uses for its RTK base view.
 * • Memory-safe: destroy() removes the L.map instance, nulls all references,
 *   and removes the injected info card DOM node so the GC can reclaim RAM
 *   when the operator closes the RTK panel.
 *
 * Dependencies: Leaflet CSS + JS must be loaded before this file.
 * Place in: wwwroot/js/rtkMapInterop.js
 * Include:  <script src="/js/rtkMapInterop.js"></script>  (after leaflet.js)
 */

window.rtkMapInterop = (() => {

    // ── Module-level state ───────────────────────────────────────────────────
    // All variables are private to this IIFE closure — impossible to conflict
    // with the primary flight map's Leaflet state.
    let _map = null;
    let _marker = null;
    let _circle = null;
    let _phaseBadge = null;   // DOM node for the phase overlay badge
    let _containerId = null;
    let _lastPhase = null;

    // ── Tile layer URLs ──────────────────────────────────────────────────────
    // Primary: Esri World Imagery — satellite, free, no API key required.
    const TILE_SAT_URL = 'https://server.arcgisonline.com/ArcGIS/' +
        'rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}';
    const TILE_SAT_ATTR = '© Esri & contributors';

    // Label overlay: place names / roads rendered over the satellite layer.
    const TILE_LBL_URL = 'https://services.arcgisonline.com/ArcGIS/rest/services/' +
        'Reference/World_Boundaries_and_Places/MapServer/tile/{z}/{y}/{x}';
    const TILE_LBL_ATTR = '© Esri';

    // ── Phase-adaptive visual config ─────────────────────────────────────────
    // The marker color, circle opacity, and badge text all adapt to the
    // current RtkPhase so the operator can see survey convergence at a glance.
    const PHASE_CONFIG = {
        Survey: { color: '#58a6ff', fillOpacity: 0.10, badgeText: 'SURVEY-IN', badgeCss: 'background:rgba(88,166,255,0.15);color:#58a6ff;border:1px solid rgba(88,166,255,0.4)' },
        Fixed: { color: '#f0b429', fillOpacity: 0.12, badgeText: 'LOCKED', badgeCss: 'background:rgba(240,180,41,0.15);color:#f0b429;border:1px solid rgba(240,180,41,0.4)' },
        Injecting: { color: '#00f2ff', fillOpacity: 0.14, badgeText: 'INJECTING', badgeCss: 'background:rgba(0,242,255,0.10);color:#00f2ff;border:1px solid rgba(0,242,255,0.4)' },
        _default: { color: '#8b949e', fillOpacity: 0.08, badgeText: 'CONNECTING', badgeCss: 'background:rgba(139,148,158,0.12);color:#8b949e;border:1px solid rgba(139,148,158,0.3)' },
    };

    function phaseConf(phase) {
        return PHASE_CONFIG[phase] ?? PHASE_CONFIG._default;
    }

    // ── Zoom level from accuracy ─────────────────────────────────────────────
    // Maps survey accuracy (metres) to an appropriate Leaflet zoom level so
    // the accuracy circle is visible but there is enough map context around it.
    function zoomFor(accuracyM) {
        if (accuracyM <= 1) return 19;
        if (accuracyM <= 5) return 18;
        if (accuracyM <= 20) return 17;
        if (accuracyM <= 100) return 15;
        return 13;
    }

    // ── Crosshair SVG marker ─────────────────────────────────────────────────
    // A precision reticle icon — color adapts to the current phase.
    function makeIcon(color) {
        const svg = `
<svg xmlns="http://www.w3.org/2000/svg" width="40" height="40" viewBox="0 0 40 40">
  <circle cx="20" cy="20" r="14" fill="none" stroke="${color}" stroke-width="1.5" opacity="0.9"/>
  <circle cx="20" cy="20" r="3"  fill="${color}"/>
  <line x1="20" y1="2"  x2="20" y2="11" stroke="${color}" stroke-width="1.5" stroke-linecap="round"/>
  <line x1="20" y1="29" x2="20" y2="38" stroke="${color}" stroke-width="1.5" stroke-linecap="round"/>
  <line x1="2"  y1="20" x2="11" y2="20" stroke="${color}" stroke-width="1.5" stroke-linecap="round"/>
  <line x1="29" y1="20" x2="38" y2="20" stroke="${color}" stroke-width="1.5" stroke-linecap="round"/>
  <line x1="9"  y1="9"  x2="13" y2="13" stroke="${color}" stroke-width="1"   stroke-linecap="round" opacity="0.55"/>
  <line x1="31" y1="9"  x2="27" y2="13" stroke="${color}" stroke-width="1"   stroke-linecap="round" opacity="0.55"/>
  <line x1="9"  y1="31" x2="13" y2="27" stroke="${color}" stroke-width="1"   stroke-linecap="round" opacity="0.55"/>
  <line x1="31" y1="31" x2="27" y2="27" stroke="${color}" stroke-width="1"   stroke-linecap="round" opacity="0.55"/>
</svg>`;
        return L.divIcon({ html: svg, className: '', iconSize: [40, 40], iconAnchor: [20, 20] });
    }

    // ── Phase badge DOM node ─────────────────────────────────────────────────
    function buildPhaseBadge(container, phase, accuracyM, alt) {
        const conf = phaseConf(phase);
        const accText = (accuracyM > 0 && accuracyM < 9000) ? `${accuracyM.toFixed(3)} m` : '—';
        const altText = (alt !== null && alt !== undefined) ? `${alt.toFixed(2)} m` : '—';

        const el = document.createElement('div');
        el.id = 'rtk-map-badge';
        el.style.cssText =
            'position:absolute;top:8px;left:50%;transform:translateX(-50%);z-index:500;' +
            'display:flex;gap:6px;align-items:center;pointer-events:none;';
        el.innerHTML =
            `<span style="font-family:monospace;font-size:0.6rem;font-weight:700;letter-spacing:1px;` +
            `padding:3px 8px;border-radius:20px;${conf.badgeCss}">${conf.badgeText}</span>` +
            `<span style="font-family:monospace;font-size:0.6rem;color:#8b949e;` +
            `background:rgba(13,17,23,0.75);padding:2px 6px;border-radius:4px;">` +
            `acc ${accText}</span>`;

        // Position relative on container
        container.style.position = 'relative';
        container.appendChild(el);
        return el;
    }

    // ── PUBLIC: initRtkMiniMap ───────────────────────────────────────────────
    /**
     * Allocates a new Leaflet instance inside `containerId` and renders
     * a crosshair marker + accuracy circle.
     *
     * @param {string} containerId  id of the <div> that will host the map
     * @param {number} lat          WGS-84 latitude in decimal degrees
     * @param {number} lng          WGS-84 longitude in decimal degrees
     * @param {number} accuracyM    Survey-In accuracy in metres (0.2 = 20 cm)
     * @param {number} alt          Ellipsoidal height in metres
     * @param {string} phase        RtkPhase enum string: "Survey"|"Fixed"|"Injecting"
     *
     * Idempotent: if called again on the same containerId, delegates to update().
     */
    function initRtkMiniMap(containerId, lat, lng, accuracyM, alt, phase) {
        const el = document.getElementById(containerId);
        if (!el) {
            console.warn('[rtkMapInterop] Container not found:', containerId);
            return;
        }

        // Idempotent: same container → just update
        if (_map && _containerId === containerId) {
            updateRtkMiniMap(lat, lng, accuracyM, alt, phase);
            return;
        }

        // Different container OR first call → destroy any existing instance first
        destroyRtkMiniMap();
        _containerId = containerId;

        const conf = phaseConf(phase);
        const zoom = zoomFor(accuracyM);

        // ── Create isolated Leaflet map instance ─────────────────────────────
        // attributionControl: false — we render attribution in the badge to
        // keep the tiny map uncluttered.
        _map = L.map(el, {
            center: [lat, lng],
            zoom: zoom,
            zoomControl: false,         // keep the UI minimal
            attributionControl: false,
            scrollWheelZoom: true,
            dragging: true,
            touchZoom: true,
        });

        // ── Tile layers ──────────────────────────────────────────────────────
        L.tileLayer(TILE_SAT_URL, {
            maxZoom: 20, maxNativeZoom: 19,
            attribution: TILE_SAT_ATTR
        }).addTo(_map);

        L.tileLayer(TILE_LBL_URL, {
            maxZoom: 20, maxNativeZoom: 19,
            attribution: TILE_LBL_ATTR,
            opacity: 0.75
        }).addTo(_map);

        // ── Accuracy circle ──────────────────────────────────────────────────
        // min radius 0.3m so the circle is always visible even at RTK accuracy
        _circle = L.circle([lat, lng], {
            radius: Math.max(accuracyM, 0.3),
            color: conf.color,
            weight: 1.5,
            opacity: 0.85,
            fillColor: conf.color,
            fillOpacity: conf.fillOpacity,
            interactive: false,
        }).addTo(_map);

        // ── Crosshair marker ─────────────────────────────────────────────────
        _marker = L.marker([lat, lng], {
            icon: makeIcon(conf.color),
            interactive: false,
            zIndexOffset: 1000,
        }).addTo(_map);

        // ── Phase badge ──────────────────────────────────────────────────────
        _phaseBadge = buildPhaseBadge(el, phase, accuracyM, alt);
        _lastPhase = phase;

        // Leaflet needs a size recalculation after mounting into a flex container
        // or hidden parent. 150 ms gives the Blazor render cycle time to finish.
        setTimeout(() => { if (_map) _map.invalidateSize(); }, 150);
    }

    // ── PUBLIC: updateRtkMiniMap ─────────────────────────────────────────────
    /**
     * Updates the marker position, accuracy circle, and badge text WITHOUT
     * rebuilding any tiles or flashing the map canvas.
     *
     * Called from Blazor's OnAfterRenderAsync on every RtkState change.
     * Because it mutates existing Leaflet objects in place, the visual update
     * is smooth and imperceptible — no reload, no white flash.
     *
     * @param {number} lat        Updated latitude
     * @param {number} lng        Updated longitude
     * @param {number} accuracyM  Updated accuracy (metres)
     * @param {number} alt        Updated ellipsoidal height (metres)
     * @param {string} phase      Updated RtkPhase string
     */
    function updateRtkMiniMap(lat, lng, accuracyM, alt, phase) {
        if (!_map) return;

        const conf = phaseConf(phase);
        const latlng = L.latLng(lat, lng);

        // ── Marker: update position and recolor if phase changed ─────────────
        _marker.setLatLng(latlng);
        if (phase !== _lastPhase) {
            _marker.setIcon(makeIcon(conf.color));
        }

        // ── Accuracy circle: resize and recolor ──────────────────────────────
        _circle.setLatLng(latlng);
        _circle.setRadius(Math.max(accuracyM, 0.3));
        if (phase !== _lastPhase) {
            _circle.setStyle({
                color: conf.color,
                fillColor: conf.color,
                fillOpacity: conf.fillOpacity,
            });
        }

        // ── Pan map to follow the position (smooth, 400 ms) ──────────────────
        _map.panTo(latlng, { animate: true, duration: 0.4 });

        // Auto-zoom when accuracy improves significantly (e.g. 50m → 2m)
        const idealZoom = zoomFor(accuracyM);
        if (Math.abs(_map.getZoom() - idealZoom) >= 2) {
            _map.setZoom(idealZoom, { animate: true });
        }

        // ── Phase badge: rebuild if phase or accuracy changed ─────────────────
        if (phase !== _lastPhase || _phaseBadge) {
            const old = document.getElementById('rtk-map-badge');
            if (old) old.remove();

            const el = document.getElementById(_containerId);
            if (el) _phaseBadge = buildPhaseBadge(el, phase, accuracyM, alt);
        }

        _lastPhase = phase;
    }

    // ── PUBLIC: destroyRtkMiniMap ────────────────────────────────────────────
    /**
     * Cleanly tears down the Leaflet instance and frees all browser memory.
     * Called from Blazor's IDisposable.Dispose() and from SetTab() when the
     * operator navigates away from the Fixed Coords view.
     *
     * Safe to call multiple times (idempotent).
     */
    function destroyRtkMiniMap() {
        if (_phaseBadge) {
            _phaseBadge.remove();
            _phaseBadge = null;
        }
        if (_map) {
            // Remove() calls layer.onRemove() on all tiles, stopping all network
            // requests and releasing canvas/WebGL contexts.
            _map.remove();
            _map = null;
            _marker = null;
            _circle = null;
        }
        _containerId = null;
        _lastPhase = null;
    }

    // ── Expose public API ────────────────────────────────────────────────────
    return {
        initRtkMiniMap,
        updateRtkMiniMap,
        destroyRtkMiniMap,
    };

})();