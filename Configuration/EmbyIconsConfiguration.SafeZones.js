define([], function () {
    'use strict';

    var _zones = [];
    var _canvas = null;
    var _container = null;
    var _img = null;
    var _isDragging = false;
    var _justFinishedDrag = false;
    var _dragStart = null;
    var _dragCurrent = null;
    var _onChangeCallback = null;

    var ZONE_COLORS = [
        'rgba(255, 80, 80, 0.35)',
        'rgba(80, 140, 255, 0.35)',
        'rgba(255, 200, 50, 0.35)',
        'rgba(140, 80, 255, 0.35)',
        'rgba(50, 200, 140, 0.35)',
    ];
    var ZONE_BORDER = [
        'rgba(255, 80, 80, 0.85)',
        'rgba(80, 140, 255, 0.85)',
        'rgba(255, 200, 50, 0.85)',
        'rgba(140, 80, 255, 0.85)',
        'rgba(50, 200, 140, 0.85)',
    ];

    function getCanvasCoords(e) {
        var rect = _canvas.getBoundingClientRect();
        var clientX = e.touches ? e.touches[0].clientX : e.clientX;
        var clientY = e.touches ? e.touches[0].clientY : e.clientY;
        return {
            x: Math.max(0, Math.min(1, (clientX - rect.left) / rect.width)) * 100,
            y: Math.max(0, Math.min(1, (clientY - rect.top) / rect.height)) * 100
        };
    }

    function onMouseDown(e) {
        if (e.button !== undefined && e.button !== 0) return;
        e.preventDefault();
        _isDragging = true;
        _dragStart = getCanvasCoords(e);
        _dragCurrent = _dragStart;
        redraw();
    }

    function onMouseMove(e) {
        if (!_isDragging) return;
        e.preventDefault();
        _dragCurrent = getCanvasCoords(e);
        redraw();
    }

    function onMouseUp(e) {
        if (!_isDragging) return;
        e.preventDefault();
        _isDragging = false;
        if (_dragStart && _dragCurrent) {
            var x = Math.min(_dragStart.x, _dragCurrent.x);
            var y = Math.min(_dragStart.y, _dragCurrent.y);
            var w = Math.abs(_dragCurrent.x - _dragStart.x);
            var h = Math.abs(_dragCurrent.y - _dragStart.y);
            if (w > 1 && h > 1) {
                _zones.push({ X: x, Y: y, Width: w, Height: h, Tag: '' });
                _justFinishedDrag = true;
                if (_onChangeCallback) _onChangeCallback();
                renderZoneList();
            }
        }
        _dragStart = null;
        _dragCurrent = null;
        redraw();
    }

    function onCanvasClick(e) {
        if (_justFinishedDrag) {
            _justFinishedDrag = false;
            return;
        }
        if (_isDragging) return;
        var coords = getCanvasCoords(e);
        for (var i = _zones.length - 1; i >= 0; i--) {
            var z = _zones[i];
            if (coords.x >= z.X && coords.x <= z.X + z.Width &&
                coords.y >= z.Y && coords.y <= z.Y + z.Height) {
                _zones.splice(i, 1);
                if (_onChangeCallback) _onChangeCallback();
                renderZoneList();
                redraw();
                return;
            }
        }
    }

    function redraw() {
        if (!_canvas) return;
        var w = _canvas.width;
        var h = _canvas.height;
        var ctx = _canvas.getContext('2d');
        ctx.clearRect(0, 0, w, h);

        for (var i = 0; i < _zones.length; i++) {
            var z = _zones[i];
            var px = z.X * w / 100;
            var py = z.Y * h / 100;
            var pw = z.Width * w / 100;
            var ph = z.Height * h / 100;
            ctx.fillStyle = ZONE_COLORS[i % ZONE_COLORS.length];
            ctx.fillRect(px, py, pw, ph);
            ctx.strokeStyle = ZONE_BORDER[i % ZONE_BORDER.length];
            ctx.lineWidth = 2;
            ctx.strokeRect(px + 1, py + 1, pw - 2, ph - 2);
            ctx.fillStyle = 'rgba(255,255,255,0.85)';
            ctx.font = 'bold ' + Math.max(11, Math.min(16, ph * 0.3)) + 'px sans-serif';
            ctx.textBaseline = 'middle';
            ctx.textAlign = 'center';
            ctx.fillText('\u00D7', px + pw / 2, py + ph / 2);
        }

        if (_isDragging && _dragStart && _dragCurrent) {
            var dx = Math.min(_dragStart.x, _dragCurrent.x) * w / 100;
            var dy = Math.min(_dragStart.y, _dragCurrent.y) * h / 100;
            var dw = Math.abs(_dragCurrent.x - _dragStart.x) * w / 100;
            var dh = Math.abs(_dragCurrent.y - _dragStart.y) * h / 100;
            ctx.fillStyle = 'rgba(255, 255, 100, 0.25)';
            ctx.fillRect(dx, dy, dw, dh);
            ctx.strokeStyle = 'rgba(255, 255, 100, 0.9)';
            ctx.lineWidth = 2;
            ctx.setLineDash([6, 3]);
            ctx.strokeRect(dx + 1, dy + 1, dw - 2, dh - 2);
            ctx.setLineDash([]);
        }
    }

    function syncCanvasSize() {
        if (!_canvas || !_img) return;
        _canvas.width = _img.clientWidth || _img.naturalWidth || 300;
        _canvas.height = _img.clientHeight || _img.naturalHeight || 450;
        redraw();
    }

    function renderZoneList() {
        var listEl = document.getElementById('safeZoneList');
        if (!listEl) return;
        if (_zones.length === 0) {
            listEl.innerHTML = '<p class="fieldDescription" style="margin: 0; font-style: italic;">No safe zones defined.</p>';
            return;
        }
        var html = '<div style="display: flex; flex-direction: column; gap: 0.5em;">';
        for (var i = 0; i < _zones.length; i++) {
            var z = _zones[i];
            var isGlobal = !z.Tag;
            var colorSwatch = '<span style="display:inline-block;width:12px;height:12px;border-radius:2px;background:' + ZONE_BORDER[i % ZONE_BORDER.length] + ';margin-right:6px;vertical-align:middle;flex-shrink:0;"></span>';
            var tagVal = (z.Tag || '').replace(/"/g, '&quot;');
            var tagHidden = isGlobal ? 'display:none;' : '';
            html += '<div style="display:flex;align-items:center;gap:0.75em;padding:0.4em 0.75em;background:rgba(255,255,255,0.06);border-radius:6px;">'
                + colorSwatch
                + '<span style="font-size:0.9em;white-space:nowrap;">Zone ' + (i + 1) + ' &mdash; '
                + 'x: ' + z.X.toFixed(1) + '%, y: ' + z.Y.toFixed(1) + '%, '
                + 'w: ' + z.Width.toFixed(1) + '%, h: ' + z.Height.toFixed(1) + '%'
                + '</span>'
                + '<label style="display:flex;align-items:center;gap:0.3em;font-size:0.85em;white-space:nowrap;cursor:pointer;flex-shrink:0;">'
                + '<input type="checkbox" class="safeZoneGlobalCheckbox" data-zone-index="' + i + '"' + (isGlobal ? ' checked' : '') + ' style="cursor:pointer;" />'
                + 'Global'
                + '</label>'
                + '<input type="text" class="safeZoneTagInput" data-zone-index="' + i + '" placeholder="Tag" value="' + tagVal + '" style="flex:1;font-size:0.85em;padding:0.2em 0.4em;background:rgba(255,255,255,0.08);border:1px solid rgba(255,255,255,0.2);border-radius:4px;color:inherit;min-width:80px;' + tagHidden + '" />'
                + '<button type="button" is="emby-button" class="raised button-cancel btnRemoveSafeZone" data-zone-index="' + i + '" style="padding:0.2em 0.6em;min-width:0;flex-shrink:0;">'
                + '<i class="md-icon" style="font-size:1em;">delete</i>'
                + '</button>'
                + '</div>';
        }
        html += '</div>';
        listEl.innerHTML = html;

        listEl.querySelectorAll('.btnRemoveSafeZone').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var idx = parseInt(btn.getAttribute('data-zone-index'), 10);
                _zones.splice(idx, 1);
                if (_onChangeCallback) _onChangeCallback();
                renderZoneList();
                redraw();
            });
        });

        listEl.querySelectorAll('.safeZoneGlobalCheckbox').forEach(function (checkbox) {
            checkbox.addEventListener('change', function () {
                var idx = parseInt(checkbox.getAttribute('data-zone-index'), 10);
                var tagInput = listEl.querySelector('.safeZoneTagInput[data-zone-index="' + idx + '"]');
                if (checkbox.checked) {
                    _zones[idx].Tag = '';
                    if (tagInput) tagInput.style.display = 'none';
                } else {
                    if (tagInput) tagInput.style.display = '';
                    _zones[idx].Tag = tagInput ? tagInput.value : '';
                }
                if (_onChangeCallback) _onChangeCallback();
            });
        });

        listEl.querySelectorAll('.safeZoneTagInput').forEach(function (input) {
            input.addEventListener('input', function () {
                var idx = parseInt(input.getAttribute('data-zone-index'), 10);
                _zones[idx].Tag = input.value;
                if (_onChangeCallback) _onChangeCallback();
            });
        });
    }

    function init(view, onChangeFn) {
        _canvas = view.querySelector('#safeZoneCanvas');
        _container = view.querySelector('#safeZoneEditorContainer');
        _img = view.querySelector('#safeZonePreviewImage');
        _onChangeCallback = onChangeFn;

        if (!_canvas || !_img) return;

        _img.src = ApiClient.getUrl('/EmbyIcons/StaticImage/preview.png', { api_key: ApiClient.accessToken() });

        function setupSize() {
            syncCanvasSize();
        }

        if (_img.complete && _img.naturalWidth > 0) {
            setupSize();
        } else {
            _img.addEventListener('load', setupSize);
        }

        _canvas.addEventListener('mousedown', onMouseDown);
        _canvas.addEventListener('mousemove', onMouseMove);
        document.addEventListener('mouseup', onMouseUp);
        _canvas.addEventListener('click', onCanvasClick);

        _canvas.addEventListener('touchstart', onMouseDown, { passive: false });
        _canvas.addEventListener('touchmove', onMouseMove, { passive: false });
        _canvas.addEventListener('touchend', onMouseUp);

        var clearBtn = view.querySelector('#btnClearAllSafeZones');
        if (clearBtn) {
            clearBtn.addEventListener('click', function () {
                _zones = [];
                if (_onChangeCallback) _onChangeCallback();
                renderZoneList();
                redraw();
            });
        }

        window.addEventListener('resize', syncCanvasSize);
    }

    function load(zones) {
        _zones = (zones || []).map(function (z) {
            return { X: z.X, Y: z.Y, Width: z.Width, Height: z.Height, Tag: z.Tag || '' };
        });
        renderZoneList();
        redraw();
    }

    function getSafeZones() {
        return _zones.map(function (z) {
            return { X: z.X, Y: z.Y, Width: z.Width, Height: z.Height, Tag: z.Tag || '' };
        });
    }

    function destroy() {
        if (_canvas) {
            _canvas.removeEventListener('mousedown', onMouseDown);
            _canvas.removeEventListener('mousemove', onMouseMove);
            _canvas.removeEventListener('click', onCanvasClick);
            _canvas.removeEventListener('touchstart', onMouseDown);
            _canvas.removeEventListener('touchmove', onMouseMove);
            _canvas.removeEventListener('touchend', onMouseUp);
        }
        document.removeEventListener('mouseup', onMouseUp);
        window.removeEventListener('resize', syncCanvasSize);
        _canvas = null;
        _container = null;
        _img = null;
        _zones = [];
        _isDragging = false;
        _justFinishedDrag = false;
        _onChangeCallback = null;
    }

    return {
        init: init,
        load: load,
        getSafeZones: getSafeZones,
        destroy: destroy
    };
});
