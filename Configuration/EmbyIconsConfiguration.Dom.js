define([], function () {
    'use strict';

    function createFilenameMappingRow(keyword, iconName, applyToMovies, applyToSeries, applyToSeasons, applyToEpisodes, iconAlignment, priority, horizontalLayout) {
        const newRow = document.createElement('div');
        newRow.classList.add('filenameMappingRow');
        newRow.style.display = 'flex';
        newRow.style.gap = '1em';
        newRow.style.alignItems = 'flex-start';
        newRow.style.marginBottom = '1.5em';
        newRow.style.padding = '1em';
        newRow.style.backgroundColor = 'rgba(255,255,255,0.02)';
        newRow.style.borderRadius = '8px';

        const moviesChecked = (applyToMovies !== false) ? 'checked' : '';
        const seriesChecked = (applyToSeries !== false) ? 'checked' : '';
        const seasonsChecked = (applyToSeasons !== false) ? 'checked' : '';
        const episodesChecked = (applyToEpisodes !== false) ? 'checked' : '';
        const horizontalChecked = (horizontalLayout !== false) ? 'checked' : '';
        
        const alignment = iconAlignment || 'BottomRight';
        const priorityVal = priority || 13;

        newRow.innerHTML = `
            <div style="flex-grow: 1; display: flex; flex-direction: column; gap: 1em;">
                <div style="display: flex; gap: 1em;">
                    <div class="inputContainer" style="flex-grow: 1; margin: 0;">
                        <input is="emby-input" type="text" label="Keyword:" value="${keyword || ''}" class="txtFilenameKeyword" />
                        <div class="fieldDescription">Case-insensitive text to find in the filename.</div>
                    </div>
                    <div class="inputContainer" style="flex-grow: 1; margin: 0;">
                        <input is="emby-input" type="text" label="Icon Name:" value="${iconName || ''}" class="txtFilenameIconName" />
                        <div class="fieldDescription">e.g., 'remux' for 'source.remux.png'</div>
                    </div>
                </div>
                <div style="display: grid; grid-template-columns: 2fr 1fr; gap: 1em;">
                    <select is="emby-select" label="Corner" class="selFilenameIconAlignment">
                        <option value="Disabled" ${alignment === 'Disabled' ? 'selected' : ''}>Disabled</option>
                        <option value="TopLeft" ${alignment === 'TopLeft' ? 'selected' : ''}>Top Left</option>
                        <option value="TopRight" ${alignment === 'TopRight' ? 'selected' : ''}>Top Right</option>
                        <option value="BottomLeft" ${alignment === 'BottomLeft' ? 'selected' : ''}>Bottom Left</option>
                        <option value="BottomRight" ${alignment === 'BottomRight' ? 'selected' : ''}>Bottom Right</option>
                    </select>
                    <select is="emby-select" label="Priority" class="selFilenamePriority">
                        ${Array.from({length: 20}, (_, i) => i + 1).map(i => 
                            `<option value="${i}" ${i === priorityVal ? 'selected' : ''}>${i}</option>`
                        ).join('')}
                    </select>
                </div>
                <div style="display: flex; gap: 1.5em; flex-wrap: wrap;">
                    <label style="display: flex; align-items: center; gap: 0.5em; cursor: pointer;">
                        <input is="emby-checkbox" type="checkbox" class="chkApplyToMovies" ${moviesChecked} />
                        <span>Movies</span>
                    </label>
                    <label style="display: flex; align-items: center; gap: 0.5em; cursor: pointer;">
                        <input is="emby-checkbox" type="checkbox" class="chkApplyToSeries" ${seriesChecked} />
                        <span>TV Shows</span>
                    </label>
                    <label style="display: flex; align-items: center; gap: 0.5em; cursor: pointer;">
                        <input is="emby-checkbox" type="checkbox" class="chkApplyToSeasons" ${seasonsChecked} />
                        <span>Seasons</span>
                    </label>
                    <label style="display: flex; align-items: center; gap: 0.5em; cursor: pointer;">
                        <input is="emby-checkbox" type="checkbox" class="chkApplyToEpisodes" ${episodesChecked} />
                        <span>Episodes</span>
                    </label>
                    <label style="display: flex; align-items: center; gap: 0.5em; cursor: pointer;">
                        <input is="emby-checkbox" type="checkbox" class="chkFilenameHorizontalLayout" ${horizontalChecked} />
                        <span>Layout Horizontally</span>
                    </label>
                </div>
            </div>
            <button is="emby-button" type="button" class="raised button-cancel btnDeleteFilenameMapping" title="Delete Mapping" style="flex-shrink: 0;"><span></span></button>
        `;

        return newRow;
    }

    return {
        createFilenameMappingRow: createFilenameMappingRow
    };
});