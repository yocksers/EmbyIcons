define(['baseView', 'loading', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-select'], function (BaseView, loading) {
    'use strict';

    const pluginId = "b8d0f5a4-3e96-4c0f-a6e2-9f0c2ecb5c5f";

    function View(view, params) {
        BaseView.apply(this, arguments);

        this.previewUpdateTimer = null;

        view.querySelector('.nav-settings').addEventListener('click', () => {
            view.querySelector('#settingsPage').classList.remove('hide');
            view.querySelector('#readmePage').classList.add('hide');
            view.querySelector('.nav-settings').classList.add('ui-btn-active');
            view.querySelector('.nav-readme').classList.remove('ui-btn-active');
        });

        view.querySelector('.nav-readme').addEventListener('click', () => {
            view.querySelector('#settingsPage').classList.add('hide');
            view.querySelector('#readmePage').classList.remove('hide');
            view.querySelector('.nav-settings').classList.remove('ui-btn-active');
            view.querySelector('.nav-readme').classList.add('ui-btn-active');
        });

        view.querySelector('.embyIconsForm').addEventListener('submit', (e) => {
            e.preventDefault();
            this.saveData();
            return false;
        });

        view.querySelector('.embyIconsForm').addEventListener('change', () => {
            if (this.previewUpdateTimer) {
                clearTimeout(this.previewUpdateTimer);
            }
            this.previewUpdateTimer = setTimeout(() => this.updatePreview(), 400);
        });

        view.querySelector('#btnSelectIconsFolder').addEventListener('click', this.selectIconsFolder.bind(this));
        view.querySelector('#btnClearCache').addEventListener('click', this.clearCache.bind(this));

        const opacitySlider = view.querySelector('#rngCommunityScoreBackgroundOpacity');
        const opacityValue = view.querySelector('#valCommunityScoreBackgroundOpacity');
        if (opacitySlider && opacityValue) {
            opacitySlider.addEventListener('input', (e) => {
                opacityValue.textContent = e.target.value + '%';
            });
        }
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.clearCache = function () {
        loading.show();
        ApiClient.ajax({
            type: "POST",
            url: ApiClient.getUrl("EmbyIcons/RefreshCache")
        }).then(() => {
            loading.hide();
            require(['toast'], function (toast) {
                toast('Icon cache cleared successfully. Posters will update as you browse.');
            });
        }).catch(() => {
            loading.hide();
            require(['toast'], function (toast) {
                toast({
                    type: 'error',
                    text: 'Error clearing icon cache.'
                });
            });
        });
    };

    View.prototype.selectIconsFolder = function () {
        var instance = this;

        require(['directorybrowser'], function (directorybrowser) {

            var picker = new directorybrowser(instance.view);

            picker.show({
                header: 'Select Icons Folder',
                path: instance.view.querySelector('#txtIconsFolder').value,
                callback: function (path) {
                    if (path) {
                        const txtIconsFolder = instance.view.querySelector('#txtIconsFolder');
                        txtIconsFolder.value = path;
                        txtIconsFolder.dispatchEvent(new Event('change', {
                            bubbles: true,
                            cancelable: true
                        }));
                    }
                    picker.close();
                }
            });
        });
    };

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        this.loadData();
    };

    View.prototype.loadData = function () {
        loading.show();
        const view = this.view;
        const self = this;

        Promise.all([
            ApiClient.getPluginConfiguration(pluginId),
            ApiClient.getVirtualFolders()
        ]).then(function ([config, virtualFolders]) {
            view.querySelector('#txtIconsFolder').value = config.IconsFolder || '';

            const ignoredLibraryTypes = ['music', 'collections'];
            const filteredLibraries = virtualFolders.Items.filter(lib => {
                if (!lib.CollectionType) {
                    return true;
                }
                return !ignoredLibraryTypes.includes(lib.CollectionType.toLowerCase());
            });

            self.populateLibraries(filteredLibraries, config.SelectedLibraries);
            view.querySelector('#txtSelectedLibraries').value = config.SelectedLibraries || '';

            view.querySelector('#chkShowAudioIcons').checked = config.ShowAudioIcons;
            view.querySelector('#chkShowSubtitleIcons').checked = config.ShowSubtitleIcons;
            view.querySelector('#chkShowAudioChannelIcons').checked = config.ShowAudioChannelIcons;
            view.querySelector('#chkShowAudioCodecIcons').checked = config.ShowAudioCodecIcons;
            view.querySelector('#chkShowVideoFormatIcons').checked = config.ShowVideoFormatIcons;
            view.querySelector('#chkShowVideoCodecIcons').checked = config.ShowVideoCodecIcons;
            view.querySelector('#chkShowTagIcons').checked = config.ShowTagIcons;
            view.querySelector('#chkShowResolutionIcons').checked = config.ShowResolutionIcons;
            view.querySelector('#chkShowCommunityScoreIcon').checked = config.ShowCommunityScoreIcon;

            view.querySelector('#chkShowOverlaysForEpisodes').checked = config.ShowOverlaysForEpisodes;
            view.querySelector('#chkShowSeriesIconsIfAllEpisodesHaveLanguage').checked = config.ShowSeriesIconsIfAllEpisodesHaveLanguage;
            view.querySelector('#chkUseSeriesLiteMode').checked = config.UseSeriesLiteMode;

            view.querySelector('#selAudioIconAlignment').value = config.AudioIconAlignment || 'TopLeft';
            view.querySelector('#selSubtitleIconAlignment').value = config.SubtitleIconAlignment || 'BottomLeft';
            view.querySelector('#selChannelIconAlignment').value = config.ChannelIconAlignment || 'TopLeft';
            view.querySelector('#selAudioCodecIconAlignment').value = config.AudioCodecIconAlignment || 'TopLeft';
            view.querySelector('#selVideoFormatIconAlignment').value = config.VideoFormatIconAlignment || 'TopRight';
            view.querySelector('#selVideoCodecIconAlignment').value = config.VideoCodecIconAlignment || 'TopRight';
            view.querySelector('#selTagIconAlignment').value = config.TagIconAlignment || 'BottomLeft';
            view.querySelector('#selResolutionIconAlignment').value = config.ResolutionIconAlignment || 'BottomRight';
            view.querySelector('#selCommunityScoreIconAlignment').value = config.CommunityScoreIconAlignment || 'TopRight';
            view.querySelector('#selCommunityScoreBackgroundShape').value = config.CommunityScoreBackgroundShape || 'None';
            view.querySelector('#txtCommunityScoreBackgroundColor').value = config.CommunityScoreBackgroundColor || '#404040';
            const opacitySlider = view.querySelector('#rngCommunityScoreBackgroundOpacity');
            const opacityValue = view.querySelector('#valCommunityScoreBackgroundOpacity');
            if (opacitySlider && opacityValue) {
                opacitySlider.value = config.CommunityScoreBackgroundOpacity == null ? 80 : config.CommunityScoreBackgroundOpacity;
                opacityValue.textContent = opacitySlider.value + '%';
            }


            view.querySelector('#chkAudioOverlayHorizontal').checked = config.AudioOverlayHorizontal;
            view.querySelector('#chkSubtitleOverlayHorizontal').checked = config.SubtitleOverlayHorizontal;
            view.querySelector('#chkChannelOverlayHorizontal').checked = config.ChannelOverlayHorizontal;
            view.querySelector('#chkAudioCodecOverlayHorizontal').checked = config.AudioCodecOverlayHorizontal;
            view.querySelector('#chkVideoFormatOverlayHorizontal').checked = config.VideoFormatOverlayHorizontal;
            view.querySelector('#chkVideoCodecOverlayHorizontal').checked = config.VideoCodecOverlayHorizontal;
            view.querySelector('#chkTagOverlayHorizontal').checked = config.TagOverlayHorizontal;
            view.querySelector('#chkResolutionOverlayHorizontal').checked = config.ResolutionOverlayHorizontal;
            view.querySelector('#chkCommunityScoreOverlayHorizontal').checked = config.CommunityScoreOverlayHorizontal;

            view.querySelector('#txtIconSize').value = config.IconSize || 10;
            view.querySelector('#txtJpegQuality').value = config.JpegQuality || 75;
            view.querySelector('#chkEnableImageSmoothing').checked = config.EnableImageSmoothing;

            self.updatePreview();
            loading.hide();
        });
    };

    View.prototype.populateLibraries = function (libraries, selectedLibrariesCsv) {
        const view = this.view;
        const container = view.querySelector('#librarySelectionContainer');

        let html = '';
        libraries.forEach(library => {
            const checkboxId = 'chk_lib_' + library.Id;
            html += `<div class="checkboxContainer"><label><input is="emby-checkbox" type="checkbox" id="${checkboxId}" data-library-name="${library.Name}" /><span>${library.Name}</span></label></div>`;
        });
        container.innerHTML = html;

        const selectedNames = (selectedLibrariesCsv || '').split(',').map(s => s.trim().toLowerCase()).filter(Boolean);
        const selectedNamesSet = new Set(selectedNames);

        container.querySelectorAll('input[type=checkbox]').forEach(checkbox => {
            const libName = checkbox.getAttribute('data-library-name').toLowerCase();
            if (selectedNamesSet.has(libName)) {
                checkbox.checked = true;
            }
        });

        container.addEventListener('change', (e) => {
            if (e.target.type === 'checkbox') {
                const selectedNames = [];
                container.querySelectorAll('input[type=checkbox]:checked').forEach(chk => {
                    selectedNames.push(chk.getAttribute('data-library-name'));
                });
                const hiddenInput = view.querySelector('#txtSelectedLibraries');
                hiddenInput.value = selectedNames.join(',');
                hiddenInput.dispatchEvent(new Event('change', { bubbles: true, cancelable: true }));
            }
        });
    };

    View.prototype.getFormOptions = function () {
        const view = this.view;
        return {
            IconsFolder: view.querySelector('#txtIconsFolder').value,
            SelectedLibraries: view.querySelector('#txtSelectedLibraries').value,
            ShowAudioIcons: view.querySelector('#chkShowAudioIcons').checked,
            ShowSubtitleIcons: view.querySelector('#chkShowSubtitleIcons').checked,
            ShowAudioChannelIcons: view.querySelector('#chkShowAudioChannelIcons').checked,
            ShowAudioCodecIcons: view.querySelector('#chkShowAudioCodecIcons').checked,
            ShowVideoFormatIcons: view.querySelector('#chkShowVideoFormatIcons').checked,
            ShowVideoCodecIcons: view.querySelector('#chkShowVideoCodecIcons').checked,
            ShowTagIcons: view.querySelector('#chkShowTagIcons').checked,
            ShowResolutionIcons: view.querySelector('#chkShowResolutionIcons').checked,
            ShowCommunityScoreIcon: view.querySelector('#chkShowCommunityScoreIcon').checked,
            ShowOverlaysForEpisodes: view.querySelector('#chkShowOverlaysForEpisodes').checked,
            ShowSeriesIconsIfAllEpisodesHaveLanguage: view.querySelector('#chkShowSeriesIconsIfAllEpisodesHaveLanguage').checked,
            UseSeriesLiteMode: view.querySelector('#chkUseSeriesLiteMode').checked,
            AudioIconAlignment: view.querySelector('#selAudioIconAlignment').value,
            SubtitleIconAlignment: view.querySelector('#selSubtitleIconAlignment').value,
            ChannelIconAlignment: view.querySelector('#selChannelIconAlignment').value,
            AudioCodecIconAlignment: view.querySelector('#selAudioCodecIconAlignment').value,
            VideoFormatIconAlignment: view.querySelector('#selVideoFormatIconAlignment').value,
            VideoCodecIconAlignment: view.querySelector('#selVideoCodecIconAlignment').value,
            TagIconAlignment: view.querySelector('#selTagIconAlignment').value,
            ResolutionIconAlignment: view.querySelector('#selResolutionIconAlignment').value,
            CommunityScoreIconAlignment: view.querySelector('#selCommunityScoreIconAlignment').value,
            CommunityScoreBackgroundShape: view.querySelector('#selCommunityScoreBackgroundShape').value,
            CommunityScoreBackgroundColor: view.querySelector('#txtCommunityScoreBackgroundColor').value,
            CommunityScoreBackgroundOpacity: parseInt(view.querySelector('#rngCommunityScoreBackgroundOpacity').value) || 80,
            AudioOverlayHorizontal: view.querySelector('#chkAudioOverlayHorizontal').checked,
            SubtitleOverlayHorizontal: view.querySelector('#chkSubtitleOverlayHorizontal').checked,
            ChannelOverlayHorizontal: view.querySelector('#chkChannelOverlayHorizontal').checked,
            AudioCodecOverlayHorizontal: view.querySelector('#chkAudioCodecOverlayHorizontal').checked,
            VideoFormatOverlayHorizontal: view.querySelector('#chkVideoFormatOverlayHorizontal').checked,
            VideoCodecOverlayHorizontal: view.querySelector('#chkVideoCodecOverlayHorizontal').checked,
            TagOverlayHorizontal: view.querySelector('#chkTagOverlayHorizontal').checked,
            ResolutionOverlayHorizontal: view.querySelector('#chkResolutionOverlayHorizontal').checked,
            CommunityScoreOverlayHorizontal: view.querySelector('#chkCommunityScoreOverlayHorizontal').checked,
            IconSize: parseInt(view.querySelector('#txtIconSize').value) || 10,
            JpegQuality: parseInt(view.querySelector('#txtJpegQuality').value) || 75,
            EnableImageSmoothing: view.querySelector('#chkEnableImageSmoothing').checked
        };
    };

    View.prototype.saveData = function () {
        loading.show();
        const instance = this;

        ApiClient.getPluginConfiguration(pluginId).then((config) => {

            const formOptions = instance.getFormOptions();
            for (const key in formOptions) {
                config[key] = formOptions[key];
            }

            ApiClient.updatePluginConfiguration(pluginId, config).then((result) => {
                Dashboard.processPluginConfigurationUpdateResult(result);
                loading.hide();
                require(['toast'], function (toast) {
                    toast('EmbyIcons settings saved.');
                });
            }).catch(() => {
                loading.hide();
                require(['toast'], function (toast) {
                    toast({
                        type: 'error',
                        text: 'Error saving EmbyIcons settings.'
                    });
                });
            });
        });
    };

    View.prototype.updatePreview = function () {
        const view = this.view;
        const options = this.getFormOptions();

        const params = {
            OptionsJson: JSON.stringify(options),
            v: new Date().getTime()
        };

        view.querySelector('.previewImage').src = ApiClient.getUrl('EmbyIcons/Preview', params);
    };

    return View;
});