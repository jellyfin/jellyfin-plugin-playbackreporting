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

    var my_bar_chart = null;
    var filter_names = [];

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

    function precisionRound(number, precision) {
        var factor = Math.pow(10, precision);
        return Math.round(number * factor) / factor;
    }

    function draw_graph(view, local_chart, usage_data) {

        console.log("data: " + usage_data);

        //console.log(usage_data);
        var chart_labels = [];
        var chart_data = [];
        for (var key in usage_data) {
            console.log(key + " " + usage_data[key]);
            var label = (key * 5) + "-" + ((key * 5) + 4)
            chart_labels.push(label);
            chart_data.push(usage_data[key]);
        }

        var barChartData = {
            labels: chart_labels, //['Mon 00', 'Mon 01', 'Mon 02', 'Mon 03', 'Mon 04', 'Mon 05', 'Mon 06'],
            datasets: [{
                label: 'Count',
                type: "bar",
                backgroundColor: '#c39bd3',
                data: chart_data, // [10,20,30,40,50,60,70]
            }/*,
            {
                label: "Minutes",
                type: "line",
                lineTension: 0,
                borderColor: "#8e5ea2",
                data: chart_data, // [10,20,30,40,50,60,70],
                fill: false
            }*/]
        };

        var chart_canvas = view.querySelector('#duration_histogram_chart_canvas');
        var ctx = chart_canvas.getContext('2d');

        if (my_bar_chart) {
            console.log("destroy() existing chart");
            my_bar_chart.destroy();
        }

        my_bar_chart = new Chart(ctx, {
            type: 'bar',
            data: barChartData,
            options: {
                legend: {
                    display: false
                },
                title: {
                    display: true,
                    text: "Counts per 5 minute intervals"
                },
                tooltips: {
                    mode: 'index',
                    intersect: false
                },
                responsive: true,
                scales: {
                    xAxes: [{
                        stacked: false,
                    }],
                    yAxes: [{
                        stacked: false,
                        ticks: {
                            autoSkip: true,
                            beginAtZero: true,
                            callback: function (value, index, values) {
                                if (Math.floor(value) === value) {
                                    return value;
                                }
                            }
                        }
                    }]
                }
            }
        });

        console.log("Chart Done");
    }

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

            LibraryMenu.setTabs('Jellyfin.Plugin.PlaybackReporting', 4, getTabs);

            import(
                window.ApiClient.getUrl("web/ConfigurationPage", {
                  name: "Chart.bundle.min.js",
                })
            ).then((d3) => {
                // get filter types form sever
                var filter_url = window.ApiClient.getUrl("user_usage_stats/type_filter_list");
                console.log("loading types form : " + filter_url);
                window.ApiClient.getUserActivity(filter_url).then(function (filter_data) {
                    filter_names = filter_data;

                    // build filter list
                    var filter_items = "";
                    for (var x1 = 0; x1 < filter_names.length; x1++) {
                        var filter_name_01 = filter_names[x1];
                        filter_items += "<input type='checkbox' id='media_type_filter_" + filter_name_01 + "' data_fileter_name='" + filter_name_01 + "' checked> " + filter_name_01 + " ";
                    }

                    var filter_check_list = view.querySelector('#filter_check_list');
                    filter_check_list.innerHTML = filter_items;

                    for (var x2 = 0; x2 < filter_names.length; x2++) {
                        var filter_name_02 = filter_names[x2];
                        view.querySelector('#media_type_filter_' + filter_name_02).addEventListener("click", process_click);
                    }

                    var end_date = view.querySelector('#end_date');
                    end_date.value = new Date().toDateInputValue();
                    end_date.addEventListener("change", process_click);

                    var weeks = view.querySelector('#weeks');
                    weeks.addEventListener("change", process_click);

                    process_click();

                    function process_click() {
                        var filter = [];
                        for (var x3 = 0; x3 < filter_names.length; x3++) {
                            var filter_name = filter_names[x3];
                            var filter_checked = view.querySelector('#media_type_filter_' + filter_name).checked;
                            if (filter_checked) {
                                filter.push(filter_name);
                            }
                        }

                        var days = parseInt(weeks.value) * 7;
                        //Set days filter to 50 years if 'all' option is selected.
                        if (days == -7) days = 18250;

                        var url = "user_usage_stats/DurationHistogramReport?days=" + days + "&endDate=" + end_date.value + "&filter=" + filter.join(",") + "&stamp=" + new Date().getTime();
                        url = window.ApiClient.getUrl(url);
                        window.ApiClient.getUserActivity(url).then(function (usage_data) {
                            //alert("Loaded Data: " + JSON.stringify(usage_data));
                            draw_graph(view, d3, usage_data);
                        });
                    }

                });

            });

        });

        view.addEventListener('viewhide', function (e) {

        });

        view.addEventListener('viewdestroy', function (e) {

        });
    };
