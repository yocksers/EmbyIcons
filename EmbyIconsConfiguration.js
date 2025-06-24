define(['baseView', 'loading', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-select'], function (BaseView, loading) {
    'use strict';

    const pluginId = "b8d0f5a4-3e96-4c0f-a6e2-9f0c2ecb5c5f";

    function View(view, params) {
        BaseView.apply(this, arguments);

        this.previewUpdateTimer = null;

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
    }

    Object.assign(View.prototype, BaseView.prototype);

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

        ApiClient.getPluginConfiguration(pluginId).then((config) => {

            view.querySelector('#txtIconsFolder').value = config.IconsFolder || '';
            view.querySelector('#txtSelectedLibraries').value = config.SelectedLibraries || '';
            view.querySelector('#chkRefreshIconCacheNow').checked = config.RefreshIconCacheNow || false;

            view.querySelector('#chkShowAudioIcons').checked = config.ShowAudioIcons;
            view.querySelector('#chkShowSubtitleIcons').checked = config.ShowSubtitleIcons;
            view.querySelector('#chkShowAudioChannelIcons').checked = config.ShowAudioChannelIcons;
            view.querySelector('#chkShowVideoFormatIcons').checked = config.ShowVideoFormatIcons;
            view.querySelector('#chkShowResolutionIcons').checked = config.ShowResolutionIcons;
            view.querySelector('#chkShowCommunityScoreIcon').checked = config.ShowCommunityScoreIcon;

            view.querySelector('#chkShowOverlaysForEpisodes').checked = config.ShowOverlaysForEpisodes;
            view.querySelector('#chkShowSeriesIconsIfAllEpisodesHaveLanguage').checked = config.ShowSeriesIconsIfAllEpisodesHaveLanguage;
            view.querySelector('#chkUseSeriesLiteMode').checked = config.UseSeriesLiteMode;

            view.querySelector('#selAudioIconAlignment').value = config.AudioIconAlignment || 'TopLeft';
            view.querySelector('#selSubtitleIconAlignment').value = config.SubtitleIconAlignment || 'BottomLeft';
            view.querySelector('#selChannelIconAlignment').value = config.ChannelIconAlignment || 'TopLeft';
            view.querySelector('#selVideoFormatIconAlignment').value = config.VideoFormatIconAlignment || 'TopRight';
            view.querySelector('#selResolutionIconAlignment').value = config.ResolutionIconAlignment || 'BottomRight';
            view.querySelector('#selCommunityScoreIconAlignment').value = config.CommunityScoreIconAlignment || 'TopRight';

            view.querySelector('#chkAudioOverlayHorizontal').checked = config.AudioOverlayHorizontal;
            view.querySelector('#chkSubtitleOverlayHorizontal').checked = config.SubtitleOverlayHorizontal;
            view.querySelector('#chkChannelOverlayHorizontal').checked = config.ChannelOverlayHorizontal;
            view.querySelector('#chkVideoFormatOverlayHorizontal').checked = config.VideoFormatOverlayHorizontal;
            view.querySelector('#chkResolutionOverlayHorizontal').checked = config.ResolutionOverlayHorizontal;
            view.querySelector('#chkCommunityScoreOverlayHorizontal').checked = config.CommunityScoreOverlayHorizontal;

            view.querySelector('#txtIconSize').value = config.IconSize || 10;
            view.querySelector('#txtJpegQuality').value = config.JpegQuality || 75;
            view.querySelector('#chkEnableImageSmoothing').checked = config.EnableImageSmoothing;

            this.updatePreview();
            loading.hide();
        });
    };

    View.prototype.getFormOptions = function () {
        const view = this.view;
        return {
            IconsFolder: view.querySelector('#txtIconsFolder').value,
            SelectedLibraries: view.querySelector('#txtSelectedLibraries').value,
            RefreshIconCacheNow: view.querySelector('#chkRefreshIconCacheNow').checked,
            ShowAudioIcons: view.querySelector('#chkShowAudioIcons').checked,
            ShowSubtitleIcons: view.querySelector('#chkShowSubtitleIcons').checked,
            ShowAudioChannelIcons: view.querySelector('#chkShowAudioChannelIcons').checked,
            ShowVideoFormatIcons: view.querySelector('#chkShowVideoFormatIcons').checked,
            ShowResolutionIcons: view.querySelector('#chkShowResolutionIcons').checked,
            ShowCommunityScoreIcon: view.querySelector('#chkShowCommunityScoreIcon').checked,
            ShowOverlaysForEpisodes: view.querySelector('#chkShowOverlaysForEpisodes').checked,
            ShowSeriesIconsIfAllEpisodesHaveLanguage: view.querySelector('#chkShowSeriesIconsIfAllEpisodesHaveLanguage').checked,
            UseSeriesLiteMode: view.querySelector('#chkUseSeriesLiteMode').checked,
            AudioIconAlignment: view.querySelector('#selAudioIconAlignment').value,
            SubtitleIconAlignment: view.querySelector('#selSubtitleIconAlignment').value,
            ChannelIconAlignment: view.querySelector('#selChannelIconAlignment').value,
            VideoFormatIconAlignment: view.querySelector('#selVideoFormatIconAlignment').value,
            ResolutionIconAlignment: view.querySelector('#selResolutionIconAlignment').value,
            CommunityScoreIconAlignment: view.querySelector('#selCommunityScoreIconAlignment').value,
            AudioOverlayHorizontal: view.querySelector('#chkAudioOverlayHorizontal').checked,
            SubtitleOverlayHorizontal: view.querySelector('#chkSubtitleOverlayHorizontal').checked,
            ChannelOverlayHorizontal: view.querySelector('#chkChannelOverlayHorizontal').checked,
            VideoFormatOverlayHorizontal: view.querySelector('#chkVideoFormatOverlayHorizontal').checked,
            ResolutionOverlayHorizontal: view.querySelector('#chkResolutionOverlayHorizontal').checked,
            CommunityScoreOverlayHorizontal: view.querySelector('#chkCommunityScoreOverlayHorizontal').checked,
            IconSize: parseInt(view.querySelector('#txtIconSize').value) || 10,
            JpegQuality: parseInt(view.querySelector('#txtJpegQuality').value) || 75,
            EnableImageSmoothing: view.querySelector('#chkEnableImageSmoothing').checked
        };
    };

    View.prototype.saveData = function () {
        loading.show();

        ApiClient.getPluginConfiguration(pluginId).then((config) => {

            const formOptions = this.getFormOptions();
            for (const key in formOptions) {
                config[key] = formOptions[key];
            }

            ApiClient.updatePluginConfiguration(pluginId, config).then((result) => {
                Dashboard.processPluginConfigurationUpdateResult(result);
                require(['toast'], function (toast) {
                    toast('EmbyIcons settings saved.');
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