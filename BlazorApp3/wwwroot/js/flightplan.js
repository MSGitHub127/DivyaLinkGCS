// wwwroot/js/flightplan.js
// Leaflet integration for DivyaLink flight plan drawing and airspace overlays.
//
// ARCHITECTURE
// ─────────────────────────────────────────────────────────────────────────
//  window.divya.draw   — polygon drawing state machine
//  window.divya.layers — map layer management (DGCA zones, OpenAIP, drawn polygon)
//
// DRAWING STATE MACHINE
//   idle      → user hasn't started drawing
//   drawing   → user is clicking to place vertices
//   complete  → polygon is closed, coords sent to C# via JSInvokable
//   confirmed → C# has validated; result rendered on map
//
// COORDINATE HANDOFF
//   JS → C# : DotNet.invokeMethodAsync("BlazorApp3", "OnPolygonDrawn", dto)
//             where dto = { coordinates: [{lat,lon},...], areaKm2: number }
//   C# → JS : window.divya.draw.setValidationResult(result)

(function () {
    'use strict';

    // Guard: only initialise once even if Blazor hot-reloads the script
    if (window.divya) return;

    window.divya = {};

    // ── CONSTANTS ─────────────────────────────────────────────────────────────

    const DRAW_VERTEX_STYLE = {
        radius: 5, color: '#f0b429', fillColor: '#f0b429',
        fillOpacity: 0.9, weight: 2
    };
    const DRAW_GHOST_VERTEX_STYLE = {
        radius: 4, color: '#58a6ff', fillColor: '#58a6ff',
        fillOpacity: 0.6, weight: 1
    };
    const DRAW_LINE_STYLE = {
        color: '#f0b429', weight: 2, dashArray: '6 4', opacity: 0.85
    };
    const POLYGON_PENDING_STYLE = {
        color: '#58a6ff', weight: 2.5, fillColor: '#58a6ff', fillOpacity: 0.12,
        dashArray: null
    };
    const POLYGON_APPROVED_STYLE = {
        color: '#3fb950', weight: 2.5, fillColor: '#3fb950', fillOpacity: 0.15
    };
    const POLYGON_REJECTED_STYLE = {
        color: '#f85149', weight: 2.5, fillColor: '#f85149', fillOpacity: 0.15
    };
    const VIOLATION_STYLE = {
        color: '#f85149', weight: 1.5, fillColor: '#f85149', fillOpacity: 0.25,
        dashArray: '4 3'
    };

    // ── DRAWING ENGINE ────────────────────────────────────────────────────────

    window.divya.draw = (function () {
        let _map = null;   // Leaflet map instance (set by initFlightPlanDrawing)
        let _state = 'idle';
        let _vertices = [];     // Array of L.LatLng (user-placed points)
        let _vertexMarkers = [];    // Visual dot markers for each vertex
        let _ghostMarker = null;   // Follows the cursor before click
        let _guidePolyline = null;  // Rubber-band line from last vertex to cursor
        let _previewPolygon = null; // Filled polygon preview during drawing
        let _finalPolygon = null;   // Confirmed polygon after C# callback
        let _violationLayers = [];  // DGCA violation zone overlays

        // ── Public: initialise on a map instance ─────────────────────────────

        function init(map) {
            _map = map;
        }

        // ── Public: start drawing mode ────────────────────────────────────────

        function startDrawing() {
            if (!_map) { console.error('[divya.draw] Map not initialised'); return; }
            if (_state === 'drawing') return;

            clearAll();
            _state = 'drawing';

            _map.getContainer().style.cursor = 'crosshair';

            // Click: add vertex
            _map.on('click', _onMapClick);
            // Mousemove: rubber-band guide
            _map.on('mousemove', _onMouseMove);
            // Double-click: complete polygon (fires after two 'click' events)
            _map.on('dblclick', _onDoubleClick);

            console.log('[divya.draw] Drawing mode started');
        }

        // ── Public: programmatically cancel drawing ────────────────────────────

        function cancelDrawing() {
            if (_state !== 'drawing') return;
            _cleanupDrawingHandlers();
            clearAll();
            _state = 'idle';
            console.log('[divya.draw] Drawing cancelled');
        }

        // ── Public: clear everything from the map ─────────────────────────────

        function clearAll() {
            _vertices = [];

            _vertexMarkers.forEach(m => _map && _map.removeLayer(m));
            _vertexMarkers = [];

            if (_ghostMarker) { _map.removeLayer(_ghostMarker); _ghostMarker = null; }
            if (_guidePolyline) { _map.removeLayer(_guidePolyline); _guidePolyline = null; }
            if (_previewPolygon) { _map.removeLayer(_previewPolygon); _previewPolygon = null; }
            if (_finalPolygon) { _map.removeLayer(_finalPolygon); _finalPolygon = null; }

            _violationLayers.forEach(l => _map && _map.removeLayer(l));
            _violationLayers = [];

            if (_map) _map.getContainer().style.cursor = '';
        }

        // ── Public: apply validation result from C# ────────────────────────────
        // Called by the Blazor component after DgcaAirspaceService returns.
        // `result` shape: { status: "APPROVED"|"REJECTED"|"CONDITIONAL", violations: [...] }

        function setValidationResult(result) {
            if (!_finalPolygon || !_map) return;

            const style = result.status === 'APPROVED'
                ? POLYGON_APPROVED_STYLE
                : POLYGON_REJECTED_STYLE;
            _finalPolygon.setStyle(style);

            // Overlay each violation zone on the map
            if (result.violations && Array.isArray(result.violations)) {
                result.violations.forEach(v => {
                    if (!v.geometry) return;
                    try {
                        const layer = L.geoJSON(v.geometry, {
                            style: VIOLATION_STYLE
                        }).addTo(_map);
                        layer.bindTooltip(v.zoneName || v.zoneType || 'Restricted Zone', {
                            sticky: true,
                            className: 'divya-tooltip-violation'
                        });
                        _violationLayers.push(layer);
                    } catch (e) {
                        console.warn('[divya.draw] Could not render violation geometry:', e);
                    }
                });
            }
        }

        // ── Private: map event handlers ───────────────────────────────────────

        function _onMapClick(e) {
            if (_state !== 'drawing') return;

            // Prevent the double-click completion from adding an extra vertex.
            // The dblclick event fires after two click events — we discard the
            // second single-click that would otherwise add a duplicate point.
            if (_onMapClick._pendingDoubleClick) {
                _onMapClick._pendingDoubleClick = false;
                return;
            }

            const latlng = e.latlng;
            _vertices.push(latlng);

            // Vertex marker
            const marker = L.circleMarker(latlng, DRAW_VERTEX_STYLE)
                .addTo(_map);

            // First vertex gets a "click to close" tooltip
            if (_vertices.length === 1) {
                marker.bindTooltip('Start point', { permanent: false });
            } else if (_vertices.length >= 3) {
                // Check if user clicked near the first vertex — close polygon
                const firstPx = _map.latLngToLayerPoint(_vertices[0]);
                const thisPx = _map.latLngToLayerPoint(latlng);
                const dist = firstPx.distanceTo(thisPx);
                if (dist < 14) { _completePolygon(); return; }
            }

            _vertexMarkers.push(marker);
            _updateGuides(latlng);
        }
        _onMapClick._pendingDoubleClick = false;

        function _onMouseMove(e) {
            if (_state !== 'drawing' || _vertices.length === 0) return;

            const cursor = e.latlng;

            // Ghost vertex at cursor
            if (!_ghostMarker) {
                _ghostMarker = L.circleMarker(cursor, DRAW_GHOST_VERTEX_STYLE).addTo(_map);
            } else {
                _ghostMarker.setLatLng(cursor);
            }

            // Rubber-band line: last confirmed vertex → cursor
            const lastVertex = _vertices[_vertices.length - 1];
            if (!_guidePolyline) {
                _guidePolyline = L.polyline([lastVertex, cursor], DRAW_LINE_STYLE).addTo(_map);
            } else {
                _guidePolyline.setLatLngs([lastVertex, cursor]);
            }

            // Filled preview polygon (3+ vertices)
            if (_vertices.length >= 2) {
                const previewLatLngs = [..._vertices, cursor];
                if (!_previewPolygon) {
                    _previewPolygon = L.polygon(previewLatLngs, {
                        ...POLYGON_PENDING_STYLE, dashArray: '6 4'
                    }).addTo(_map);
                } else {
                    _previewPolygon.setLatLngs(previewLatLngs);
                }
            }
        }

        function _onDoubleClick(e) {
            if (_state !== 'drawing' || _vertices.length < 3) return;

            // Signal to the next click handler to discard the trailing click
            _onMapClick._pendingDoubleClick = true;
            _completePolygon();
        }

        // ── Private: finish drawing, extract coords, call C# ─────────────────

        function _completePolygon() {
            if (_vertices.length < 3) return;

            _cleanupDrawingHandlers();
            _state = 'complete';

            // Remove guides
            if (_ghostMarker) { _map.removeLayer(_ghostMarker); _ghostMarker = null; }
            if (_guidePolyline) { _map.removeLayer(_guidePolyline); _guidePolyline = null; }
            if (_previewPolygon) { _map.removeLayer(_previewPolygon); _previewPolygon = null; }

            // Render the confirmed polygon
            _finalPolygon = L.polygon(_vertices, POLYGON_PENDING_STYLE).addTo(_map);
            _map.fitBounds(_finalPolygon.getBounds(), { padding: [40, 40] });

            // Build DTO for C#
            const coords = _vertices.map(v => ({ lat: v.lat, lon: v.lng }));
            const areaKm2 = _computeAreaKm2(_vertices);
            const dto = { coordinates: coords, areaKm2 };

            console.log('[divya.draw] Polygon complete:', coords.length, 'vertices,',
                areaKm2.toFixed(3), 'km²');

            // ── HAND OFF TO C# VIA JSInvokable ────────────────────────────────
            // Blazor component must have a static [JSInvokable("OnPolygonDrawn")] method.
            // Prefer DotNetObjectReference (instance call) — safer when multiple
            // browser tabs are open.  Falls back to the static approach if the
            // component hasn't set the ref yet (e.g., during hot-reload).
            const ref = window.divya.draw._dotNetRef;
            if (ref) {
                ref.invokeMethodAsync('OnPolygonDrawn', dto)
                    .catch(err => console.error('[divya.draw] Instance JSInvokable error:', err));
            } else {
                DotNet.invokeMethodAsync('BlazorApp3', 'OnPolygonDrawn', dto)
                    .catch(err => console.error('[divya.draw] Static JSInvokable error:', err));
            }
        }

        // ── Private: helpers ──────────────────────────────────────────────────

        function _cleanupDrawingHandlers() {
            if (!_map) return;
            _map.off('click', _onMapClick);
            _map.off('mousemove', _onMouseMove);
            _map.off('dblclick', _onDoubleClick);
            _map.getContainer().style.cursor = '';
        }

        function _updateGuides(newLatLng) {
            // Called after adding a new vertex — guide line updates on next mousemove
        }

        // Spherical excess area (same formula as GeoJsonUtility.cs)
        function _computeAreaKm2(latlngs) {
            const R = 6371;
            let area = 0;
            const n = latlngs.length;
            for (let i = 0; i < n; i++) {
                const c1 = latlngs[i];
                const c2 = latlngs[(i + 1) % n];
                const lon1 = c1.lng * Math.PI / 180;
                const lon2 = c2.lng * Math.PI / 180;
                const lat1 = c1.lat * Math.PI / 180;
                const lat2 = c2.lat * Math.PI / 180;
                area += (lon2 - lon1) * (2 + Math.sin(lat1) + Math.sin(lat2));
            }
            return Math.abs(area * R * R / 2);
        }

        return {
            init, startDrawing, cancelDrawing, clearAll, setValidationResult,
            _dotNetRef: null
        };  // writable slot; set by divya.attachMapAndRef()
    })();

    // ── MAP LAYER MANAGEMENT ──────────────────────────────────────────────────

    window.divya.layers = (function () {
        let _map = null;
        let _openAipLayer = null;
        let _dgcaLayer = null;
        let _layerControl = null;

        function init(map) {
            _map = map;
        }

        // ── OpenAIP hybrid layer ──────────────────────────────────────────────
        // OpenAIP provides general aviation hazards: small airstrips, helipads,
        // military areas, glider sites — as a free raster tile overlay.
        //
        // Registration: https://www.openaip.net/users/register (free tier)
        // API Key: stored in appsettings.json → VideoStreaming:OpenAipApiKey
        // IMPORTANT: The tile URL changed in 2024. Use the /api/data/openaip/ path.

        function addOpenAipLayer(apiKey) {
            if (!_map) return;
            if (_openAipLayer) { _map.removeLayer(_openAipLayer); }

            // OpenAIP raster tiles (free, up to 25k requests/day on free tier)
            _openAipLayer = L.tileLayer(
                `https://api.tiles.openaip.net/api/data/openaip/{z}/{x}/{y}.png?apiKey=${apiKey}`,
                {
                    attribution: '&copy; <a href="https://www.openaip.net">OpenAIP</a>',
                    maxZoom: 14,
                    minZoom: 4,
                    opacity: 0.80,
                    // Tile error handler: silently fail on missing tiles (rural areas)
                    errorTileUrl: 'data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7'
                }
            );

            _openAipLayer.addTo(_map);
            console.log('[divya.layers] OpenAIP layer added');
            return _openAipLayer;
        }

        function removeOpenAipLayer() {
            if (_openAipLayer && _map) {
                _map.removeLayer(_openAipLayer);
                _openAipLayer = null;
            }
        }

        function setOpenAipOpacity(value) {
            if (_openAipLayer) _openAipLayer.setOpacity(value);
        }

        // ── DGCA Red/Yellow/Green zone overlay ───────────────────────────────
        // DGCA provides official airspace zones as WMS or GeoJSON.
        // This renders violation zones returned in the API response.

        function renderDgcaZones(geojsonFeatureCollection) {
            if (!_map || !geojsonFeatureCollection) return;
            if (_dgcaLayer) { _map.removeLayer(_dgcaLayer); }

            _dgcaLayer = L.geoJSON(geojsonFeatureCollection, {
                style: feature => {
                    const zoneType = feature.properties?.zoneType?.toUpperCase() ?? 'UNKNOWN';
                    switch (zoneType) {
                        case 'RED': return { color: '#f85149', fillColor: '#f85149', fillOpacity: 0.2, weight: 2 };
                        case 'YELLOW': return { color: '#e3b341', fillColor: '#e3b341', fillOpacity: 0.15, weight: 1.5 };
                        case 'GREEN': return { color: '#3fb950', fillColor: '#3fb950', fillOpacity: 0.1, weight: 1 };
                        default: return { color: '#8b949e', fillColor: '#8b949e', fillOpacity: 0.1, weight: 1 };
                    }
                },
                onEachFeature: (feature, layer) => {
                    const p = feature.properties ?? {};
                    const popup =
                        `<div style="font-family:monospace;font-size:12px;">
                           <strong>${p.zoneName ?? 'Unnamed Zone'}</strong><br/>
                           Type: <span style="color:${p.zoneType === 'RED' ? '#f85149' : '#e3b341'}">${p.zoneType}</span><br/>
                           ${p.description ?? ''}
                         </div>`;
                    layer.bindPopup(popup);
                }
            }).addTo(_map);

            return _dgcaLayer;
        }

        function clearDgcaZones() {
            if (_dgcaLayer && _map) {
                _map.removeLayer(_dgcaLayer);
                _dgcaLayer = null;
            }
        }

        return {
            init,
            addOpenAipLayer, removeOpenAipLayer, setOpenAipOpacity,
            renderDgcaZones, clearDgcaZones
        };
    })();

    // ── BLAZOR INTEROP ENTRY POINTS ───────────────────────────────────────────
    // Called by Blazor components via JS.InvokeVoidAsync / JS.InvokeAsync

    window.divya.initFlightPlan = function (map) {
        window.divya.draw.init(map);
        window.divya.layers.init(map);
        console.log('[divya] Flight plan drawing engine initialised on map');
    };

    // Single call that both attaches the existing Leaflet map instance AND registers
    // the Blazor DotNetObjectReference so JS can call back into the component instance.
    // Called from FlightPlan.razor OnAfterRenderAsync — replaces the unsafe eval() approach.
    window.divya.attachMapAndRef = function (dotNetRef) {
        if (!window._gcsMap) {
            console.error('[divya] attachMapAndRef: window._gcsMap is not set yet. ' +
                'Ensure initMap() has completed before calling this.');
            return false;
        }
        window.divya.draw.init(window._gcsMap);
        window.divya.layers.init(window._gcsMap);
        window.divya.draw._dotNetRef = dotNetRef;
        console.log('[divya] Map attached and DotNetRef registered');
        return true;
    };

    window.divya.startPolygonDraw = function () {
        window.divya.draw.startDrawing();
    };

    window.divya.cancelPolygonDraw = function () {
        window.divya.draw.cancelDrawing();
    };

    window.divya.clearFlightPlan = function () {
        window.divya.draw.clearAll();
    };

    window.divya.applyValidationResult = function (resultJson) {
        const result = typeof resultJson === 'string'
            ? JSON.parse(resultJson)
            : resultJson;
        window.divya.draw.setValidationResult(result);
    };

    window.divya.addOpenAipLayer = function (apiKey) {
        window.divya.layers.addOpenAipLayer(apiKey);
    };

    window.divya.setOpenAipOpacity = function (value) {
        window.divya.layers.setOpenAipOpacity(value);
    };

    window.divya.renderDgcaZones = function (geojsonString) {
        const geojson = JSON.parse(geojsonString);
        window.divya.layers.renderDgcaZones(geojson);
    };

    console.log('[divya] flightplan.js loaded');
})();
// ═══════════════════════════════════════════════════════════════════════════════
// DGCA AIRSPACE PIPELINE — paste this block at the bottom of flightplan.js
// ═══════════════════════════════════════════════════════════════════════════════
// Waits for both window._gcsMap (Leaflet) and window.airspace.loadZones (renderer)
// to exist before attaching the moveend listener.
// Polls every 300 ms — stops as soon as both are ready.
// Safe to call multiple times; the listener is attached exactly once.
// ═══════════════════════════════════════════════════════════════════════════════

(function initAirspacePipeline() {

    let _debounceTimer = null;  // Holds the 500 ms debounce timeout handle
    let _attached = false; // Guard: attach the listener exactly once

    // ── Step 1: fetch zones for the current map bounds ───────────────────────
    async function fetchAndRenderZones() {
        const map = window._gcsMap;
        if (!map) return;

        const b = map.getBounds();
        const minLat = b.getSouth();
        const minLon = b.getWest();
        const maxLat = b.getNorth();
        const maxLon = b.getEast();

        const url = `/api/airspace?minLat=${minLat}&minLon=${minLon}&maxLat=${maxLat}&maxLon=${maxLon}`;

        try {
            const res = await fetch(url);
            if (!res.ok) {
                console.warn(`[airspace] API ${res.status} — skipping render.`);
                return;
            }
            const geoJsonText = await res.text();
            window.airspace.loadZones(geoJsonText);
        } catch (err) {
            console.error('[airspace] Fetch error:', err);
        }
    }

    // ── Step 2: attach the debounced moveend listener ────────────────────────
    function attachListener() {
        if (_attached) return;
        _attached = true;

        const map = window._gcsMap;

        map.on('moveend', () => {
            // Cancel any pending debounce so rapid drags don't spam the API
            if (_debounceTimer) clearTimeout(_debounceTimer);
            _debounceTimer = setTimeout(fetchAndRenderZones, 500);
        });

        // Fire immediately so zones appear on the current view without panning
        fetchAndRenderZones();

        console.info('[airspace] Pipeline ready — moveend listener active.');
    }

    // ── Step 3: poll until map + renderer are both ready ────────────────────
    // Blazor renders asynchronously; _gcsMap may not exist at script-parse time.
    // We check every 300 ms and give up after 30 s (100 attempts).
    let attempts = 0;
    const MAX_ATTEMPTS = 100;

    const waiter = setInterval(() => {
        attempts++;

        const mapReady = window._gcsMap
            && typeof window._gcsMap.getBounds === 'function';
        const rendererReady = window.airspace
            && typeof window.airspace.loadZones === 'function';

        if (mapReady && rendererReady) {
            clearInterval(waiter);
            attachListener();
            return;
        }

        if (attempts >= MAX_ATTEMPTS) {
            clearInterval(waiter);
            console.warn('[airspace] Gave up waiting for _gcsMap / airspace.loadZones after 30 s.');
        }
    }, 300);

})(); // IIFE — runs immediately when flightplan.js is parsed by the browser
