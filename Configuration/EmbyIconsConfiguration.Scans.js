define(['loading', 'toast'], function (loading, toast) {
    'use strict';

    function pollScanProgress(instance, scanType, button, container) {
        clearInterval(instance.progressPollInterval);

        instance.progressPollInterval = setInterval(async () => {
            try {
                const progress = await ApiClient.ajax({ type: "GET", url: ApiClient.getUrl(instance.apiRoutes.ScanProgress, { ScanType: scanType }), dataType: "json" });

                if (progress.IsComplete) {
                    clearInterval(instance.progressPollInterval);
                    return;
                }

                const percent = progress.Total > 0 ? Math.round((progress.Current / progress.Total) * 100) : 0;
                const message = `${percent}% - ${progress.Message}`;

                if (button) button.querySelector('span').textContent = message;
                if (container) container.innerHTML = `<p>${message}</p>`;

            } catch (err) {
                console.error('Error polling for scan progress', err);
                clearInterval(instance.progressPollInterval);
            }
        }, 2000);
    }

    async function runIconScan(instance) {
        loading.show();
        instance.dom.btnRunIconScan.disabled = true;
        instance.dom.btnRunIconScan.querySelector('span').textContent = 'Starting Scan...';

        const container = instance.dom.iconManagerReportContainer;
        container.innerHTML = '<p>Scanning your library... This may take several minutes on the first run.</p>';

        pollScanProgress(instance, "IconManager", instance.dom.btnRunIconScan, container);

        try {
            const report = await ApiClient.ajax({ type: "GET", url: ApiClient.getUrl(instance.apiRoutes.IconManagerReport), dataType: "json" });
            renderIconManagerReport(instance, report);
        } catch (error) {
            console.error('Error getting icon manager report', error);
            container.innerHTML = '<p style="color: #ff4444;">An error occurred while generating the report. Please check the server logs.</p>';
        } finally {
            loading.hide();
            clearInterval(instance.progressPollInterval);
            instance.dom.btnRunIconScan.disabled = false;
            instance.dom.btnRunIconScan.querySelector('span').textContent = 'Scan Library & Icons';
        }
    }

    function renderIconManagerReport(instance, report) {
        const container = instance.dom.iconManagerReportContainer;
        const friendlyNames = { Language: 'Audio Languages', Subtitle: 'Subtitle Languages', Channel: 'Audio Channels', AudioCodec: 'Audio Codecs', VideoCodec: 'Video Codecs', VideoFormat: 'Video Formats', Resolution: 'Resolutions', AspectRatio: 'Aspect Ratios', Tag: 'Custom Tags', ParentalRating: 'Parental Ratings' };
        const prefixMap = { Language: "lang.", Subtitle: "sub.", Channel: "ch.", AudioCodec: "ac.", VideoCodec: "vc.", VideoFormat: "hdr.", Resolution: "res.", AspectRatio: "ar.", Tag: "tag.", ParentalRating: "pr." };

        const htmlParts = [];
        htmlParts.push(`<p class="fieldDescription">Report generated on: ${new Date(report.ReportDate).toLocaleString()}</p>`);

        for (const groupName in report.Groups) {
            if (Object.prototype.hasOwnProperty.call(report.Groups, groupName) && friendlyNames[groupName]) {
                const group = report.Groups[groupName];
                const librarySet = new Set(group.FoundInLibrary.map(i => i.toLowerCase()));
                const folderSet = new Set(group.FoundInFolder.map(i => i.toLowerCase()));
                const found = [...folderSet].filter(i => librarySet.has(i)).sort();
                const missing = [...librarySet].filter(i => !folderSet.has(i)).sort();
                const unused = [...folderSet].filter(i => !librarySet.has(i)).sort();

                if (found.length === 0 && missing.length === 0 && unused.length === 0) continue;

                htmlParts.push(`<div class="paper-card" style="margin-top: 1.5em; padding: 1em 1.5em;">`);
                htmlParts.push(`<h3 style="margin-top: 0; cursor: pointer;" onclick="this.nextElementSibling.style.display = this.nextElementSibling.style.display === 'none' ? 'block' : 'none';">${friendlyNames[groupName]}</h3>`);
                htmlParts.push(`<div class="collapsible-content" style="display: none;">`);

                if (missing.length > 0) {
                    htmlParts.push(`<h4><span style="color: #ffc107;">!</span> Missing Icons (${missing.length})</h4><p class="fieldDescription">Your library needs these icons, but they were not found in your custom folder. You can add them or rely on the built-in fallback icons (if enabled).</p>`);
                    htmlParts.push(`<div class="icon-grid">${missing.map(i => `<code>${prefixMap[groupName] || ''}${i}.png</code>`).join('')}</div>`);
                }
                if (found.length > 0) {
                    htmlParts.push(`<h4 style="margin-top: 1.5em;"><span style="color: #4CAF50;">✓</span> Found Icons (${found.length})</h4><p class="fieldDescription">These custom icons are correctly configured and used by your library.</p>`);
                    htmlParts.push(`<div class="icon-grid">${found.map(i => `<code>${prefixMap[groupName] || ''}${i}.png</code>`).join('')}</div>`);
                }
                if (unused.length > 0) {
                    htmlParts.push(`<h4 style="margin-top: 1.5em;"><span style="color: #888;">-</span> Unused Icons (${unused.length})</h4><p class="fieldDescription">These icons exist in your folder but are not currently needed by any media.</p>`);
                    htmlParts.push(`<div class="icon-grid">${unused.map(i => `<code>${prefixMap[groupName] || ''}${i}.png</code>`).join('')}</div>`);
                }
                htmlParts.push(`</div></div>`);
            }
        }
        htmlParts.push(`<style>.icon-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(180px, 1fr)); gap: 0.5em; } .icon-grid code { background-color: rgba(128,128,128,0.2); padding: 0.2em 0.4em; border-radius: 3px; word-break: break-all; }</style>`);

        container.innerHTML = htmlParts.join('');
    }

    async function searchForSeries(instance) {
        const searchTerm = instance.dom.txtSeriesSearch.value;
        const resultsContainer = instance.dom.seriesSearchResults;

        if (searchTerm.length < 2) {
            resultsContainer.innerHTML = '';
            resultsContainer.style.display = 'none';
            return;
        }

        try {
            const results = await ApiClient.getItems(instance.currentUser.Id, { IncludeItemTypes: 'Series', Recursive: true, SearchTerm: searchTerm, Limit: 10, Fields: 'Path' });

            if (results.Items.length) {
                resultsContainer.innerHTML = results.Items.map(item => `<div class="searchResultItem" data-id="${item.Id}" data-name="${item.Name}" style="padding: 0.75em 1em; cursor: pointer;">${item.Name} <span style="color:#aaa; font-size:0.9em;">(${item.Path || 'Unknown'})</span></div>`).join('');
                resultsContainer.style.display = 'block';
            } else {
                resultsContainer.innerHTML = '<div style="padding: 0.75em 1em; color: #aaa;">No results found</div>';
                resultsContainer.style.display = 'block';
            }
        } catch (err) {
            console.error('Error searching for series', err);
            resultsContainer.style.display = 'none';
        }
    }

    function onSeriesSearchResultClick(instance, e) {
        const target = e.target.closest('.searchResultItem');
        if (target) {
            instance.selectedSeriesId = target.getAttribute('data-id');
            instance.dom.txtSeriesSearch.value = target.getAttribute('data-name');
            instance.dom.seriesSearchResults.style.display = 'none';
            instance.dom.seriesSearchResults.innerHTML = '';
        }
    }

    async function runSeriesScan(instance) {
        if (!instance.selectedSeriesId) { toast({ type: 'error', text: 'Please search for and select a TV show first.' }); return; }
        const checksToRun = Array.from(instance.dom.troubleshooterChecks).filter(cb => cb.checked).map(cb => cb.getAttribute('data-check-name')).join(',');
        if (!checksToRun) { toast({ type: 'error', text: 'Please select at least one property to check.' }); return; }

        loading.show();
        instance.dom.btnRunSeriesScan.disabled = true;
        const container = instance.dom.seriesReportContainer;
        container.innerHTML = '<p>Scanning series... This might take a moment.</p>';

        try {
            const reports = await ApiClient.ajax({ type: "GET", url: ApiClient.getUrl(instance.apiRoutes.SeriesTroubleshooter, { SeriesId: instance.selectedSeriesId, ChecksToRun: checksToRun }), dataType: "json" });
            renderSeriesReport(instance, reports);
        } catch (error) {
            console.error('Error getting series troubleshooter report', error);
            container.innerHTML = '<p style="color: #ff4444;">An error occurred while generating the report. Please check the server logs.</p>';
        } finally {
            loading.hide();
            instance.dom.btnRunSeriesScan.disabled = false;
        }
    }

    async function runFullSeriesScan(instance) {
        const checksToRun = Array.from(instance.dom.troubleshooterChecks).filter(cb => cb.checked).map(cb => cb.getAttribute('data-check-name')).join(',');
        if (!checksToRun) { toast({ type: 'error', text: 'Please select at least one property to check.' }); return; }

        loading.show();
        instance.dom.btnRunFullSeriesScan.disabled = true;
        const container = instance.dom.seriesReportContainer;
        container.innerHTML = '<p>Scanning all TV shows in your library... This can take several minutes.</p>';

        pollScanProgress(instance, "FullSeriesScan", instance.dom.btnRunFullSeriesScan, container);

        try {
            const reports = await ApiClient.ajax({ type: "GET", url: ApiClient.getUrl(instance.apiRoutes.SeriesTroubleshooter, { ChecksToRun: checksToRun }), dataType: "json" });
            renderSeriesReport(instance, reports);
        } catch (error) {
            console.error('Error getting full series troubleshooter report', error);
            container.innerHTML = '<p style="color: #ff4444;">An error occurred while generating the report. Please check the server logs.</p>';
        } finally {
            loading.hide();
            clearInterval(instance.progressPollInterval);
            instance.dom.btnRunFullSeriesScan.disabled = false;
        }
    }

    function renderSeriesReport(instance, reports) {
        const container = instance.dom.seriesReportContainer;
        if (!reports || reports.length === 0) { container.innerHTML = '<h2>Scan Complete</h2><p>No inconsistencies found for the selected criteria.</p>'; return; }

        const html = reports.map(report => {
            const mismatchChecks = report.Checks.filter(c => c.Status === 'Mismatch');
            if (mismatchChecks.length === 0) return '';

            const checksHtml = mismatchChecks.map(check => {
                const episodesHtml = check.MismatchedEpisodes.map(ep => `<li><code>${ep.EpisodeName || `Item ID: ${ep.EpisodeId}`}</code><br><span style="font-size: 0.9em;">Expected: <code class="report-code report-code-ok">${check.DominantValues.join(', ') || 'N/A'}</code>, Found: <code class="report-code report-code-warn">${ep.Actual.join(', ') || 'Nothing'}</code></span></li>`).join('');
                return `<div class="paper-card" style="padding: 1em 1.5em; margin-top: 1em;"><h4 style="margin:0; color: #ffc107;">${check.CheckName}</h4><p class="fieldDescription" style="margin: 0.5em 0;">${check.Message}</p><ul style="margin: 1em 0 0.5em; padding-left: 1.5em; list-style-type: disc;">${episodesHtml}</ul></div>`;
            }).join('');

            return `<div class="report-group collapsible" style="margin-bottom: 1em;"><div class="collapsible-header paper-card" style="padding: 1em 1.5em; cursor: pointer; display: flex; align-items: center; justify-content: space-between;"><h2 style="margin: 0;">${report.SeriesName} <span class="fieldDescription">(${report.TotalEpisodes} episodes)</span></h2><i class="md-icon collapsible-indicator" style="transition: transform 0.2s ease;">keyboard_arrow_down</i></div><div class="collapsible-content" style="display: none; padding-top: 0.5em;">${checksHtml}</div></div>`;
        }).join('');

        container.innerHTML = (html.trim() === '') ? '<h2>Scan Complete</h2><p>No inconsistencies found in any TV show for the selected criteria.</p>' : html;
        container.insertAdjacentHTML('beforeend', `<style>.report-code { background-color: rgba(128,128,128,0.2); padding: 0.2em 0.4em; border-radius: 3px; } .report-code-ok { color: #4CAF50; } .report-code-warn { color: #ffc107; }</style>`);
    }

    return {
        pollScanProgress,
        runIconScan,
        renderIconManagerReport,
        searchForSeries,
        onSeriesSearchResultClick,
        runSeriesScan,
        runFullSeriesScan,
        renderSeriesReport
    };
});
