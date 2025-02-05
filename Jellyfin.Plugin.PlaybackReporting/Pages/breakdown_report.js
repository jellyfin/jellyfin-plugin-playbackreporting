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

    var chart_instance_map = {};

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

    function draw_chart_user_count(view, local_chart, data, group_type) {

        var chart_data_labels_count = [];
        var chart_data_values_count = [];

        var chart_data_labels_time = [];
        var chart_data_values_time = [];

        data.sort(function (a, b) {
            return ((a["count"] > b["count"]) ? -1 : ((a["count"] == b["count"]) ? 0 : 1));
        });

        var count = 0;
        for (var index in data) {
            if (count++ >= 10) {
                break;
            }
            chart_data_labels_count.push(data[index]["label"]);
            chart_data_values_count.push(data[index]["count"]);
        }

        data.sort(function (a, b) {
            return ((a["time"] > b["time"]) ? -1 : ((a["time"] == b["time"]) ? 0 : 1));
        });

        count = 0;
        for (var index in data) {
            if (count++ >= 10) {
                break;
            }
            chart_data_labels_time.push(data[index]["label"]);
            chart_data_values_time.push(data[index]["time"]);
        }

        //console.log(chart_data_labels_count);
        //console.log(chart_data_values_count);
        //console.log(chart_data_labels_time);
        //console.log(chart_data_values_time);

        var chart_data_user_count = {
            labels: chart_data_labels_count,
            datasets: [{
                label: "Breakdown",
                backgroundColor: ["#d98880", "#c39bd3", "#7fb3d5", "#76d7c4", "#7dcea0", "#f7dc6f", "#f0b27a", "#d7dbdd", "#85c1e9", "#f1948a"],
                data: chart_data_values_count,
            }]
        };

        var chart_data_user_time = {
            labels: chart_data_labels_time,
            datasets: [{
                label: "Breakdown",
                backgroundColor: ["#d98880", "#c39bd3", "#7fb3d5", "#76d7c4", "#7dcea0", "#f7dc6f", "#f0b27a", "#d7dbdd", "#85c1e9", "#f1948a"],
                data: chart_data_values_time,
            }]
        };

        function tooltip_labels(tooltipItem, data) {

            var indice = tooltipItem.index;
            var label = data.labels[indice] || "";

            if (label) {
                label += ": " + seconds2time(data.datasets[0].data[indice]);
            }

            return label;
        }

        var chart_canvas_count = view.querySelector('#' + group_type + '_breakdown_count_chart_canvas');
        var cxt_count = chart_canvas_count.getContext('2d');
        if (chart_instance_map[group_type+"_count"]) {
            console.log("destroy() existing chart");
            chart_instance_map[group_type + "_count"].destroy();
        }
        chart_instance_map[group_type + "_count"] = new Chart(cxt_count, {
            type: 'pie',
            data: chart_data_user_count,
            options: {
                title: {
                    display: true,
                    text: group_type + " (Plays)"
                },
                legend: {
                    display: false
                },
                legendCallback: function (chart) {
                    var legendHtml = [];
                    legendHtml.push('<table style="width:80%">');
                    var item = chart.data.datasets[0];
                    for (var i = 0; i < item.data.length; i++) {
                        legendHtml.push('<tr>');
                        legendHtml.push('<td><div style="width: 30px; background-color:' + item.backgroundColor[i] + '">&nbsp;</div></td>');
                        legendHtml.push('<td style="width: 100%">' + chart.data.labels[i] + '</td>');
                        legendHtml.push('<td style="text-align: right;">' + item.data[i] + '</td>');
                        legendHtml.push('</tr>');
                    }
                    legendHtml.push('</table>');
                    return legendHtml.join("");
                }
            }
        });

        var chart_legend_count = view.querySelector('#' + group_type + '_breakdown_count_chart_legend');
        if (chart_legend_count != null) {
            chart_legend_count.innerHTML = chart_instance_map[group_type + "_count"].generateLegend();
        }

        if (chart_instance_map[group_type + "_time"]) {
            console.log("destroy() existing chart");
            chart_instance_map[group_type + "_time"].destroy();
        }

        var chart_canvas_time = view.querySelector('#' + group_type + '_breakdown_time_chart_canvas');
        var cxt_time = chart_canvas_time.getContext('2d');
        chart_instance_map[group_type + "_time"] = new Chart(cxt_time, {
            type: 'pie',
            data: chart_data_user_time,
            options: {
                title: {
                    display: true,
                    text: group_type + " (Time)"
                },
                tooltips: {
                    callbacks: {
                        label: tooltip_labels
                    }
                },
                legend: {
                    display: false
                },
                legendCallback: function (chart) {
                    var legendHtml = [];
                    legendHtml.push('<table style="width:80%">');
                    var item = chart.data.datasets[0];
                    for (var i = 0; i < item.data.length; i++) {
                        legendHtml.push('<tr>');
                        legendHtml.push('<td><div style="width: 30px; background-color:' + item.backgroundColor[i] + '">&nbsp;</div></td>');
                        legendHtml.push('<td style="width: 100%">' + chart.data.labels[i] + '</td>');
                        legendHtml.push('<td style="text-align: right;">' + seconds2time(item.data[i]) + '</td>');
                        legendHtml.push('</tr>');
                    }
                    legendHtml.push('</table>');
                    return legendHtml.join("");
                }
            }
        });

        var chart_legend_time = view.querySelector('#' + group_type + '_breakdown_time_chart_legend');
        if (chart_legend_time != null) {
            chart_legend_time.innerHTML = chart_instance_map[group_type + "_time"].generateLegend();
        }

        console.log("Charts Done");
    }

    function seconds2time(seconds) {
        var d = Math.floor(seconds / 86400);
        seconds = seconds - (d * 86400);
        var h = Math.floor(seconds / 3600);
        seconds = seconds - (h * 3600);
        var m = Math.floor(seconds / 60);
        var s = seconds - (m * 60);
        var time_string = "";
        if (d > 0) {
            time_string += d + ".";
        }
        time_string += padLeft(h) + ":" + padLeft(m) + ":" + padLeft(s);
        return time_string;
    }

    function padLeft(value) {
        if (value < 10) {
            return "0" + value;
        }
        else {
            return value;
        }
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

            LibraryMenu.setTabs('playback_reporting', 2, getTabs);

            import(
                window.ApiClient.getUrl("web/ConfigurationPage", {
                  name: "Chart.bundle.min.js",
                })
            ).then((d3) => {
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

                    const timezoneOffset = -(new Date().getTimezoneOffset() / 60);

                    // build user chart
                    var url = "user_usage_stats/UserId/BreakdownReport?days=" + days + "&endDate=" + end_date.value + "&stamp=" + new Date().getTime() + "&timezoneOffset=" + timezoneOffset;
                    url = window.ApiClient.getUrl(url);
                    window.ApiClient.getUserActivity(url).then(function (data) {
                        //alert("Loaded Data: " + JSON.stringify(usage_data));
                        draw_chart_user_count(view, d3, data, "User");
                    });

                    // build ItemType chart
                    var url = "user_usage_stats/ItemType/BreakdownReport?days=" + days + "&endDate=" + end_date.value + "&stamp=" + new Date().getTime() + "&timezoneOffset=" + timezoneOffset;
                    url = window.ApiClient.getUrl(url);
                    window.ApiClient.getUserActivity(url).then(function (data) {
                        //alert("Loaded Data: " + JSON.stringify(usage_data));
                        draw_chart_user_count(view, d3, data, "ItemType");
                    });

                    // build PlaybackMethod chart
                    var url = "user_usage_stats/PlaybackMethod/BreakdownReport?days=" + days + "&endDate=" + end_date.value + "&stamp=" + new Date().getTime() + "&timezoneOffset=" + timezoneOffset;
                    url = window.ApiClient.getUrl(url);
                    window.ApiClient.getUserActivity(url).then(function (data) {
                        //alert("Loaded Data: " + JSON.stringify(usage_data));
                        draw_chart_user_count(view, d3, data, "PlayMethod");
                    });

                    // build ClientName chart
                    var url = "user_usage_stats/ClientName/BreakdownReport?days=" + days + "&endDate=" + end_date.value + "&stamp=" + new Date().getTime() + "&timezoneOffset=" + timezoneOffset;
                    url = window.ApiClient.getUrl(url);
                    window.ApiClient.getUserActivity(url).then(function (data) {
                        //alert("Loaded Data: " + JSON.stringify(usage_data));
                        draw_chart_user_count(view, d3, data, "ClientName");
                    });

                    // build DeviceName chart
                    var url = "user_usage_stats/DeviceName/BreakdownReport?days=" + days + "&endDate=" + end_date.value + "&stamp=" + new Date().getTime() + "&timezoneOffset=" + timezoneOffset;
                    url = window.ApiClient.getUrl(url);
                    window.ApiClient.getUserActivity(url).then(function (data) {
                        //alert("Loaded Data: " + JSON.stringify(usage_data));
                        draw_chart_user_count(view, d3, data, "DeviceName");
                    });

                    // build TvShows chart
                    var url = "user_usage_stats/GetTvShowsReport?days=" + days + "&endDate=" + end_date.value + "&stamp=" + new Date().getTime() + "&timezoneOffset=" + timezoneOffset;
                    url = window.ApiClient.getUrl(url);
                    window.ApiClient.getUserActivity(url).then(function (data) {
                        //alert("Loaded Data: " + JSON.stringify(usage_data));
                        draw_chart_user_count(view, d3, data, "TvShows");
                    });

                    // build Movies chart
                    var url = "user_usage_stats/MoviesReport?days=" + days + "&endDate=" + end_date.value + "&stamp=" + new Date().getTime() + "&timezoneOffset=" + timezoneOffset;
                    url = window.ApiClient.getUrl(url);
                    window.ApiClient.getUserActivity(url).then(function (data) {
                        //alert("Loaded Data: " + JSON.stringify(usage_data));
                        draw_chart_user_count(view, d3, data, "Movies");
                    });
                }
            });
        });

        view.addEventListener('viewhide', function (e) {

        });

        view.addEventListener('viewdestroy', function (e) {

        });
    };

