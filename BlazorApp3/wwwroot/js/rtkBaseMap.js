/**
 * rtkBaseMap.js
 * Place in: wwwroot/js/rtkBaseMap.js
 *
 * Leaflet map bridge for the RTK "Fixed Coords" tab (Tab 1).
 * Called from RtkDrawer.razor via OnAfterRenderAsync:
 *
 *   rtkBaseMap.init(containerId, lat, lng, accuracyM, altM)
 *   rtkBaseMap.update(lat, lng, accuracyM, altM)
 *   rtkBaseMap.destroy()
 *
 * ── Design contracts ──────────────────────────────────────────────────────────
 *
 *  TILES      Esri World Imagery satellite tiles as the base layer, overlaid
 *             with the Esri World Boundaries and Places reference layer for
 *             place-name annotation.  No OpenStreetMap tiles.
 *
 *  ZOOM       The map initialises at FIXED_ZOOM (16) and this module NEVER
 *             changes the zoom level programmatically after that point.
 *             All Leaflet user-interaction zoom paths are enabled:
 *               • scroll-wheel zoom
 *               • touch pinch-to-zoom
 *               • default +/- zoom control buttons
 *               • double-click zoom
 *             The operator retains exclusive control over zoom at all times.
 *
 *  PAN        Live coordinate updates use _map.panTo() only.  No fitBounds(),
 *             flyTo(), setZoom(), or setView() are ever called after init.
 *
 *  HALO COLOR When accuracyM ≤ ACCURACY_LOCK_M (1.0) the accuracy circle
 *             border and fill shift from blue (#58a6ff) to green (#3fb950),
 *             signalling to the operator that a secure baseline is achieved.
 *             The transition is applied without a tile-layer redraw flicker by
 *             using L.Circle.setStyle() directly on the existing layer object.
 *
 *  RESIZE     A ResizeObserver is attached to the container element during
 *             init() and disconnected during destroy().  Any change to the
 *             container's bounding box (e.g. Blazor drawer panels expanding or
 *             collapsing) triggers a debounced invalidateSize() so the tile
 *             grid boundary recomputes instead of clipping.
 *
 *  IDEMPOTENT init() called again for the same container delegates to update().
 *             destroy() is safe to call when no map exists (no-op).
 *
 * Requires Leaflet (loaded globally via CDN in App.razor <head> — L namespace
 * is resident in browser memory before any component calls this module).
 */
window.rtkBaseMap = (() => {
    'use strict';

    // ── Tile sources ──────────────────────────────────────────────────────────

    /**
     * Esri World Imagery satellite base layer.
     * Tile URL scheme follows Esri ArcGIS REST MapServer conventions.
     * Note: z/y/x order in the path (NOT the standard z/x/y of OSM).
     */
    const SAT_URL =
        'https://server.arcgisonline.com/ArcGIS/rest/services/' +
        'World_Imagery/MapServer/tile/{z}/{y}/{x}';

    const SAT_ATTR =
        'Tiles &copy; Esri &mdash; Source: Esri, i-cubed, USDA, USGS, AEX, ' +
        'GeoEye, Getmapping, Aerogrid, IGN, IGP, UPR-EGP, and the GIS User Community';

    /**
     * Esri World Boundaries and Places text annotation overlay.
     * Rendered on top of the satellite base layer at opacity 0.7 so that
     * place names, borders, and road labels remain readable.
     */
    const LBL_URL =
        'https://services.arcgisonline.com/ArcGIS/rest/services/' +
        'Reference/World_Boundaries_and_Places/MapServer/tile/{z}/{y}/{x}';

    const LBL_ATTR = 'Labels &copy; Esri';

    // ── Constants ─────────────────────────────────────────────────────────────

    /** Fixed initial zoom level — never overridden by this module after init(). */
    const FIXED_ZOOM = 16;

    /** Accuracy threshold in metres below which the halo turns green. */
    const ACCURACY_LOCK_M = 1.0;

    /** Halo colour when accuracy > ACCURACY_LOCK_M (nominal blue). */
    const COLOR_NOMINAL = '#58a6ff';

    /** Halo colour when accuracy ≤ ACCURACY_LOCK_M (secure baseline green). */
    const COLOR_LOCKED = '#3fb950';

    /**
     * Minimum circle radius in metres.
     * Prevents a degenerate 0-radius circle when the receiver reports
     * sub-millimetre accuracy (division artefact or clamped telemetry).
     */
    const MIN_CIRCLE_R = 0.3;

    /** panTo animation duration in seconds. */
    const PAN_DURATION_S = 0.35;

    /**
     * Debounce delay in milliseconds for ResizeObserver-triggered invalidateSize().
     * Prevents excessive recomputes during continuous resize animations.
     */
    const RESIZE_DEBOUNCE_MS = 50;

    // ── Module state ──────────────────────────────────────────────────────────

    let _map = null;   // Leaflet Map instance
    let _marker = null;   // L.Marker — position reticle
    let _circle = null;   // L.Circle — accuracy halo
    let _badge = null;   // HTMLElement — data overlay badge
    let _containerId = null;   // string — id of the active host <div>
    let _resizeObserver = null;   // ResizeObserver — dynamic size tracking
    let _resizeTimer = null;   // setTimeout handle for resize debounce

    // ── Private helpers ───────────────────────────────────────────────────────

    /**
     * Return the accent colour applied to the accuracy halo and badge.
     *
     * @param   {number} accM  Horizontal accuracy in metres
     * @returns {string}       CSS hex colour string
     */
    function _accentColor(accM) {
        return accM <= ACCURACY_LOCK_M ? COLOR_LOCKED : COLOR_NOMINAL;
    }

    /**
     * Create a centred blue-dot SVG divIcon for the position reticle.
     * Anchored at its visual centre so it sits precisely on the coordinate
     * without pixel-offset drift at high zoom levels.
     *
     * @returns {L.DivIcon}
     */
    function _makeIcon() {
        return L.divIcon({
            className: '',   // suppress Leaflet's default white-box background
            html: `<svg width="20" height="20" viewBox="0 0 20 20"
                        xmlns="http://www.w3.org/2000/svg">
                     <circle cx="10" cy="10" r="5"
                             fill="#58a6ff" stroke="white" stroke-width="2.5"/>
                   </svg>`,
            iconSize: [20, 20],
            iconAnchor: [10, 10],
        });
    }

    /**
     * Create or refresh the accuracy / altitude data badge overlaid in the
     * lower-left corner of the map tile area.
     *
     * The badge is a plain <div> appended directly to the container element
     * (not a Leaflet control) so it stays visible above all tile layers without
     * entering Leaflet's internal DOM management.  The container is set to
     * `position: relative` on first call so absolute positioning works.
     *
     * Badge colour semantics mirror the halo: accuracy value renders in blue
     * for nominal readings and green with a "◆ LOCKED" suffix once the
     * 1.0 m threshold is crossed.
     *
     * @param {HTMLElement} containerEl  The map's host <div>
     * @param {number}      accM         Horizontal accuracy in metres
     * @param {number}      altM         Altitude above WGS-84 ellipsoid in metres
     * @returns {HTMLElement}            The badge element (created or updated)
     */
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
            // Ensure absolute positioning of the badge is relative to the map div.
            containerEl.style.position = 'relative';
            containerEl.appendChild(badge);
        }

        const color = _accentColor(accM);
        const locked = accM <= ACCURACY_LOCK_M;

        // Rebuild inner HTML on every update — fast enough at telemetry rate.
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

    /**
     * Attach a ResizeObserver to the given element.
     *
     * Any change to the element's bounding box fires a debounced
     * invalidateSize() on the Leaflet map.  This handles layout shifts caused
     * by Blazor drawer panels expanding or collapsing while Tab 1 is active —
     * without it the tile grid clips to the size measured at init() time.
     *
     * The observer is stored in _resizeObserver so destroy() can disconnect it.
     * If the browser does not support ResizeObserver (older Safari < 13.1) the
     * function is a no-op: the existing setTimeout invalidateSize() guard in
     * init() provides a basic fallback.
     *
     * @param {HTMLElement} el  The element to observe
     */
    function _attachResizeObserver(el) {
        if (typeof ResizeObserver === 'undefined') return;

        _resizeObserver = new ResizeObserver(() => {
            // Debounce: cancel any pending invalidation and reschedule.
            if (_resizeTimer !== null) {
                clearTimeout(_resizeTimer);
                _resizeTimer = null;
            }
            _resizeTimer = setTimeout(() => {
                _resizeTimer = null;
                if (_map) _map.invalidateSize({ animate: false });
            }, RESIZE_DEBOUNCE_MS);
        });

        _resizeObserver.observe(el);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /**
     * Initialise the Leaflet map inside the element identified by `containerId`.
     *
     * Tile stack
     * ──────────
     * Two Esri tile layers are added in order:
     *   1. SAT_URL  — World Imagery satellite base (full opacity)
     *   2. LBL_URL  — World Boundaries and Places annotation overlay (70% opacity)
     * The annotation layer uses `pane` order; Leaflet renders tileLayer instances
     * in the order they are added to the map, so the label overlay always sits
     * above the imagery without additional z-index management.
     *
     * Zoom policy
     * ───────────
     * The map is created at FIXED_ZOOM (16) and this call is the ONLY place
     * in this module that sets a zoom level.  After init() returns, zoom belongs
     * entirely to the operator.  Leaflet's native +/- buttons, scroll-wheel,
     * pinch-to-zoom, and double-click-to-zoom are all enabled.
     *
     * Idempotency
     * ───────────
     * If init() is called again while the map already exists for the same
     * container (e.g. OnAfterRenderAsync fires on a re-render) the call is
     * silently delegated to update() so the operator's zoom is preserved.
     *
     * Resize handling
     * ───────────────
     * A ResizeObserver is attached to the container element immediately after
     * the map is created.  It fires a debounced invalidateSize() on every
     * layout shift so the tile grid boundary stays correct regardless of
     * subsequent panel resizes.  A secondary one-shot setTimeout also fires
     * at +180 ms to catch the initial Blazor drawer open animation.
     *
     * @param {string} containerId  DOM id of the host <div>
     * @param {number} lat          Decimal degrees latitude
     * @param {number} lng          Decimal degrees longitude
     * @param {number} accuracyM    Horizontal accuracy in metres
     * @param {number} altM         Altitude above WGS-84 ellipsoid in metres
     */
    function init(containerId, lat, lng, accuracyM, altM) {
        // ── Idempotency guard ─────────────────────────────────────────────────
        if (_map && _containerId === containerId) {
            update(lat, lng, accuracyM, altM);
            return;
        }

        // Clean up any stale instance before creating a new one.
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
            zoom: FIXED_ZOOM,   // operator's starting zoom — never changed in code
            zoomControl: true,         // default +/- buttons visible at all times
            attributionControl: true,
            scrollWheelZoom: true,         // mouse wheel zoom — always on
            doubleClickZoom: true,         // double-click zoom — always on
            touchZoom: true,         // pinch-to-zoom on touch devices — always on
            dragging: true,         // pan by drag — always on
        });

        // ── Satellite base layer (Esri World Imagery) ─────────────────────────
        // maxNativeZoom=19: Esri tiles are available up to z=19; Leaflet upscales
        // beyond that from the z=19 tiles rather than requesting non-existent tiles.
        L.tileLayer(SAT_URL, {
            maxZoom: 20,
            maxNativeZoom: 19,
            attribution: SAT_ATTR,
        }).addTo(_map);

        // ── Label annotation overlay (Esri World Boundaries and Places) ───────
        // Added AFTER the satellite layer so it renders on top without z-index
        // manipulation.  opacity=0.7 keeps labels readable without obscuring
        // the imagery detail underneath.
        L.tileLayer(LBL_URL, {
            maxZoom: 20,
            maxNativeZoom: 19,
            attribution: LBL_ATTR,
            opacity: 0.7,
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
            interactive: false,   // never captures pointer events
        }).addTo(_map);

        // ── Position reticle (marker) ─────────────────────────────────────────
        _marker = L.marker([lat, lng], {
            icon: _makeIcon(),
            interactive: false,
            zIndexOffset: 1000,
        }).addTo(_map);

        // ── Data overlay badge ────────────────────────────────────────────────
        _badge = _buildBadge(el, accuracyM, altM);

        // ── ResizeObserver — dynamic tile-grid boundary adjustment ────────────
        // Fires debounced invalidateSize() on any container resize so the tile
        // grid never clips when Blazor panel layout shifts while Tab 1 is active.
        _attachResizeObserver(el);

        // ── One-shot size guard ───────────────────────────────────────────────
        // Fires after the Blazor drawer open CSS animation (≈150 ms) to catch
        // the case where Leaflet measured a 0×0 size during L.map() construction
        // because the flex container hadn't finished painting yet.
        setTimeout(() => { if (_map) _map.invalidateSize({ animate: false }); }, 180);
    }

    /**
     * Reposition the reticle and resize / recolour the accuracy halo.
     *
     * Zoom contract
     * ─────────────
     * This function NEVER calls setZoom(), setView(), flyTo(), or any other
     * Leaflet method that modifies the zoom level.  panTo() is used exclusively
     * so that the map viewport tracks the position while the operator's chosen
     * zoom level is fully preserved across every coordinate update.
     *
     * Colour transition
     * ─────────────────
     * The halo colour is evaluated on every call via _accentColor():
     *   • accuracyM > 1.0 m  → blue  (#58a6ff) — survey in progress
     *   • accuracyM ≤ 1.0 m  → green (#3fb950) — baseline secured
     *
     * L.Circle.setStyle() updates SVG path attributes in-place; it does NOT
     * remove and re-add the layer, so there is no tile-redraw flicker.
     *
     * @param {number} lat
     * @param {number} lng
     * @param {number} accuracyM
     * @param {number} altM
     */
    function update(lat, lng, accuracyM, altM) {
        if (!_map) return;

        const ll = L.latLng(lat, lng);
        const color = _accentColor(accuracyM);

        // Reposition the reticle marker.
        _marker.setLatLng(ll);

        // Resize and recolour the accuracy halo in-place (no flicker).
        _circle.setLatLng(ll);
        _circle.setRadius(Math.max(accuracyM, MIN_CIRCLE_R));
        _circle.setStyle({
            color: color,
            fillColor: color,
        });

        // Pan the viewport to the new position — zoom is NOT touched.
        _map.panTo(ll, { animate: true, duration: PAN_DURATION_S });

        // Refresh the data overlay badge.
        const el = document.getElementById(_containerId);
        if (el) _buildBadge(el, accuracyM, altM);
    }

    /**
     * Remove the Leaflet map instance and release all browser resources.
     *
     * Called from:
     *   • RtkDrawer.SetTab()  — when the operator leaves Tab 1 (Fixed Coords),
     *     so the map re-creates cleanly on next tab activation with the correct
     *     container dimensions.
     *   • RtkDrawer.Dispose() — when the component is torn down entirely.
     *
     * Safe to call when no map exists (no-op).
     */
    function destroy() {
        // Cancel any pending resize debounce timer first.
        if (_resizeTimer !== null) {
            clearTimeout(_resizeTimer);
            _resizeTimer = null;
        }

        // Disconnect the ResizeObserver before removing the map so the
        // observer cannot fire invalidateSize() on a removed map instance.
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

    // Expose the three public methods under window.rtkBaseMap.
    return { init, update, destroy };
})();