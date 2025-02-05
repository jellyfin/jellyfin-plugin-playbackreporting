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

    Date.prototype.toDateInputValue = (function () {
        var local = new Date(this);
        local.setMinutes(this.getMinutes() - this.getTimezoneOffset());
        return local.toJSON().slice(0, 10);
    });

    window.ApiClient.getUserActivity = function (url_to_get) {
        console.log("getUserActivity Url = " + url_to_get);
        return this.ajax({
            type: "GET",
            url: url_to_get,
            dataType: "json"
        });
    };

    function getTabs() {
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

    export default function (view, params) {

        // init code here
        view.addEventListener('viewshow', function (e) {

            LibraryMenu.setTabs('user_report', 0, getTabs);

            var end_date = view.querySelector('#end_date');
            end_date.value = new Date().toDateInputValue();
            end_date.addEventListener("change", process_click);

            var weeks = view.querySelector('#weeks');
            weeks.addEventListener("change", process_click);

            process_click();

            function process_click() {
                var days = parseInt(weeks.value) * 7;
                //Set days filter to 50 years if 'all' option is selected.
                if (days == -7) days = 18250;

                var url = "user_usage_stats/user_activity?days=" + days + "&endDate=" + end_date.value + "&stamp=" + new Date().getTime();
                url = window.ApiClient.getUrl(url);
                window.ApiClient.getUserActivity(url).then(function (user_data) {
                    console.log("usage_data: " + JSON.stringify(user_data));
                    var table_body = view.querySelector('#user_report_results');
                    var row_html = "";

                    for (var index = 0; index < user_data.length; ++index) {
                        var user_info = user_data[index];

                        row_html += "<tr class='detailTableBodyRow detailTableBodyRow-shaded'>";

                        var user_image = "assets/img/avatar.png";
                        if (user_info.has_image) {
                            user_image = "Users/" + user_info.user_id + "/Images/Primary?width=50";
                            user_image = window.ApiClient.getUrl(user_image);
                        }                      

                        row_html += "<td><img src='" + user_image + "' width='50px' height='50px' style='background-color: black; object-fit: cover;'></td>";
                        row_html += "<td>" + user_info.user_name + "</td>";
                        row_html += "<td>" + user_info.last_seen + "</td>";
                        row_html += "<td>" + user_info.item_name + "</td>";
                        row_html += "<td>" + user_info.client_name + "</td>";
                        row_html += "<td>" + user_info.total_count + "</td>";
                        row_html += "<td>" + user_info.total_play_time + "</td>";

                        row_html += "</tr>";
                    }

                    table_body.innerHTML = row_html;

                });
            }
        });

        view.addEventListener('viewhide', function (e) {

        });

        view.addEventListener('viewdestroy', function (e) {

        });
    };

