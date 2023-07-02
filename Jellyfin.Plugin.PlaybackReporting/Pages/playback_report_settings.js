/*
Copyright(C) 2018

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program. If not, see<http://www.gnu.org/licenses/>.
*/

const getConfigurationPageUrl = (name) => {
    return 'configurationpage?name=' + encodeURIComponent(name);
}

    window.ApiClient.getUserActivity = function (url_to_get) {
        console.log("getUserActivity Url = " + url_to_get);
        return this.ajax({
            type: "GET",
            url: url_to_get,
            dataType: "json"
        });
    };

    function setBackupPathCallBack(selectedDir, view) {
        window.ApiClient.getNamedConfiguration('playback_reporting').then(function (config) {
            config.BackupPath = selectedDir;
            console.log("New Config Settings : " + JSON.stringify(config));
            window.ApiClient.updateNamedConfiguration('playback_reporting', config);

            var backup_path_label = view.querySelector('#backup_path_label');
            backup_path_label.innerHTML = selectedDir;
        });
    }

    function loadPage(view, config) {

        console.log("Settings Page Loaded With Config : " + JSON.stringify(config));
        var max_data_age_select = view.querySelector('#max_data_age_select');
        max_data_age_select.value = config.MaxDataAge;

        var backup_files_to_keep = view.querySelector('#files_to_keep');
        backup_files_to_keep.value = config.MaxBackupFiles;

        var backup_path_label = view.querySelector('#backup_path_label');
        backup_path_label.innerHTML = config.BackupPath;
    }

    function getTabs() {
        console.log(window)
        var tabs = [
            {
                href: getConfigurationPageUrl('user_report'),
                name: 'Users'
            },
            {
                href: getConfigurationPageUrl('user_playback_report'),
                name: 'Playback'
            },
            {
                href: getConfigurationPageUrl('breakdown_report'),
                name: 'Breakdown'
            },
            {
                href: getConfigurationPageUrl('hourly_usage_report'),
                name: 'Usage'
            },
            {
                href: getConfigurationPageUrl('duration_histogram_report'),
                name: 'Duration'
            },
            {
                href: getConfigurationPageUrl('custom_query'),
                name: 'Query'
            },
            {
                href: getConfigurationPageUrl('playback_report_settings'),
                name: 'Settings'
            }];
        return tabs;
    }

    function saveBackup(view) {
        var url = "user_usage_stats/save_backup?stamp=" + new Date().getTime();
        url = window.ApiClient.getUrl(url);
        window.ApiClient.getUserActivity(url).then(function (responce_message) {
            //alert("Loaded Data: " + JSON.stringify(usage_data));
            alert(responce_message[0]);

            window.ApiClient.getNamedConfiguration('playback_reporting').then(function (config) {
                var backup_path_label = view.querySelector('#backup_path_label');
                backup_path_label.innerHTML = config.BackupPath;
            });

        });
    }

    function loadBackupFile(selectedFile, view) {
        var encoded_path = encodeURI(selectedFile);
        var url = "user_usage_stats/load_backup?backupfile=" + encoded_path + "&stamp=" + new Date().getTime();
        url = window.ApiClient.getUrl(url);
        window.ApiClient.getUserActivity(url).then(function (responce_message) {
            //alert("Loaded Data Message : " + JSON.stringify(responce_message));
            alert(responce_message[0]);
        });
    }

    function showUserList(view) {

        var url = "user_usage_stats/user_list?stamp=" + new Date().getTime();
        url = window.ApiClient.getUrl(url);
        window.ApiClient.getUserActivity(url).then(function (user_list) {
            //alert("Loaded Data: " + JSON.stringify(user_list));

            var add_user_list = view.querySelector('#user_list_for_add');
            var options_html = "";
            for (var index = 0; index < user_list.length; ++index) {
                var item_details = user_list[index];
                //if (item_details.in_list == false) {
                    options_html += "<option value='" + item_details.id + "'>" + item_details.name + "</option>";
                //}
            }
            add_user_list.innerHTML = options_html;


            var user_list_items = view.querySelector('#user_list_items');
            var list_html = "";

            for (var index = 0; index < user_list.length; ++index) {
                var item_details = user_list[index];
                if (item_details.in_list == true) {
                    list_html += "<li>" + item_details.name + "</li>";
                }
            }
            user_list_items.innerHTML = list_html;

        });
    }

    export default function (view, params) {

        // init code here
        view.addEventListener('viewshow', function (e) {

            LibraryMenu.setTabs('playplayback_report_settingsback_reporting', 6, getTabs);

            var set_backup_path = view.querySelector('#set_backup_path');
            set_backup_path.addEventListener("click", setBackupPathPicker);

            var backup_data_now = view.querySelector('#backup_data_now');
            backup_data_now.addEventListener("click", function () { saveBackup(view) });

            var load_backup_data = view.querySelector('#load_backup_data');
            load_backup_data.addEventListener("click", loadBackupDataPicker);

            var max_data_age_select = view.querySelector('#max_data_age_select');
            max_data_age_select.addEventListener("change", setting_changed);

            var backup_files_to_keep = view.querySelector('#files_to_keep');
            backup_files_to_keep.addEventListener("change", files_to_keep_changed);

            function files_to_keep_changed() {
                var max_files = backup_files_to_keep.value;
                window.ApiClient.getNamedConfiguration('playback_reporting').then(function (config) {
                    config.MaxBackupFiles = max_files;
                    console.log("New Config Settings : " + JSON.stringify(config));
                    window.ApiClient.updateNamedConfiguration('playback_reporting', config);
                });
            }

            function setting_changed() {
                var max_age = max_data_age_select.value;
                window.ApiClient.getNamedConfiguration('playback_reporting').then(function (config) {
                    config.MaxDataAge = max_age;
                    console.log("New Config Settings : " + JSON.stringify(config));
                    window.ApiClient.updateNamedConfiguration('playback_reporting', config);
                });
            }

            function loadBackupDataPicker() {
                var picker = new Dashboard.DirectoryBrowser();
                picker.show({
                    includeFiles: true,
                    callback: function (selected) {
                        picker.close();
                        loadBackupFile(selected, view);
                    },
                    header: "Select backup file to load"
                });
            }

            function setBackupPathPicker() {
                var picker = new Dashboard.DirectoryBrowser()
                picker.show({
                    includeFiles: false,
                    callback: function (selected) {
                        picker.close();
                        setBackupPathCallBack(selected, view);
                    },
                    header: "Select backup path"
                });
            }

            // remove unknown users button
            var remove_unknown_button = view.querySelector('#remove_unknown_button');
            remove_unknown_button.addEventListener("click", function () {
                var url = "user_usage_stats/user_manage/prune" + "?stamp=" + new Date().getTime();
                url = window.ApiClient.getUrl(url);
                window.ApiClient.getUserActivity(url).then(function (result) {
                    alert("Unknown user activity removed.");
                });

            });

            window.ApiClient.getNamedConfiguration('playback_reporting').then(function (config) {
                loadPage(view, config);
            });

            // user list

            var add_button = view.querySelector('#add_user_to_list');
            add_button.addEventListener("click", function () {
                var add_user_list = view.querySelector('#user_list_for_add');
                var selected_user_id = add_user_list.options[add_user_list.selectedIndex].value;
                var url = "user_usage_stats/user_manage/add?id=" + selected_user_id + "&stamp=" + new Date().getTime();
                url = window.ApiClient.getUrl(url);
                window.ApiClient.getUserActivity(url).then(function (result) {
                    //alert(result);
                    showUserList(view);
                });

            });

            var remove_button = view.querySelector('#remove_user_from_list');
            remove_button.addEventListener("click", function () {
                var add_user_list = view.querySelector('#user_list_for_add');
                var selected_user_id = add_user_list.options[add_user_list.selectedIndex].value;
                var url = "user_usage_stats/user_manage/remove?id=" + selected_user_id + "&stamp=" + new Date().getTime();
                url = window.ApiClient.getUrl(url);
                window.ApiClient.getUserActivity(url).then(function (result) {
                    //alert(result);
                    showUserList(view);
                });
            });

            showUserList(view);

        });

        view.addEventListener('viewhide', function (e) {

        });

        view.addEventListener('viewdestroy', function (e) {

        });
    };
