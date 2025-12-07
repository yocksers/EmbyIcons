define([], function () {
    'use strict';

    function createFilenameMappingRow(keyword, iconName) {
        const newRow = document.createElement('div');
        newRow.classList.add('filenameMappingRow');
        newRow.style.display = 'flex';
        newRow.style.gap = '1em';
        newRow.style.alignItems = 'center';
        newRow.style.marginBottom = '1em';

        newRow.innerHTML = `
            <div class="inputContainer" style="flex-grow: 1; margin: 0;">
                <input is="emby-input" type="text" label="Keyword:" value="${keyword || ''}" class="txtFilenameKeyword" />
                <div class="fieldDescription">Case-insensitive text to find in the filename.</div>
            </div>
            <div class="inputContainer" style="flex-grow: 1; margin: 0;">
                <input is="emby-input" type="text" label="Icon Name:" value="${iconName || ''}" class="txtFilenameIconName" />
                <div class="fieldDescription">e.g., 'remux' for 'source.remux.png'</div>
            </div>
            <button is="emby-button" type="button" class="raised button-cancel btnDeleteFilenameMapping" title="Delete Mapping"><span></span></button>
        `;

        return newRow;
    }

    return {
        createFilenameMappingRow: createFilenameMappingRow
    };
});
