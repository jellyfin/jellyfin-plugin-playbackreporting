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

    var daily_bar_chart = null;
    var hourly_bar_chart = null;
    var weekly_bar_chart = null;
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

        var days_of_week = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

        //console.log(usage_data);
        var chart_labels = [];
        var chart_data = [];
        var aggregated_hours = {};
        var aggregated_days = {};
        for (var key in usage_data) {
            //console.log(key + " " + usage_data[key]);
            var day_index = key.substring(0, 1);
            var day_name = days_of_week[day_index];
            var day_hour = key.substring(2);
            //chart_labels.push(day_name + " " + day_hour + ":00");
            chart_labels.push(day_name + " " + day_hour);
            chart_data.push(usage_data[key]);//precisionRound(usage_data[key] / 60, 2));
            var current_hour_value = 0;
            if (aggregated_hours[day_hour]) {
                current_hour_value = aggregated_hours[day_hour];
            }
            aggregated_hours[day_hour] = current_hour_value + usage_data[key];
            var current_day_value = 0;
            if (aggregated_days[day_index]) {
                current_day_value = aggregated_days[day_index];
            }
            aggregated_days[day_index] = current_day_value + usage_data[key];
        }
        //chart_labels.push("00");

        //console.log(JSON.stringify(aggregated_hours));
        //console.log(JSON.stringify(aggregated_days));

        //
        // daily bar chart data
        //
        var daily_chart_label_data = [];
        var daily_chart_point_data = [];
        var daily_days_labels = Object.keys(aggregated_days);
        daily_days_labels.sort();
        for (var daily_key_index = 0; daily_key_index < daily_days_labels.length; daily_key_index++) {
            var daily_key = daily_days_labels[daily_key_index];
            daily_chart_label_data.push(days_of_week[daily_key]);
            daily_chart_point_data.push(aggregated_days[daily_key]);
        }

        var daily_chart_data = {
            labels: daily_chart_label_data,
            datasets: [{
                label: 'Time',
                type: "bar",
                backgroundColor: '#c39bd3',
                data: daily_chart_point_data
            }]
        };

        //
        // hourly chart data
        //
        var hourly_chart_label_data = [];
        var hourly_chart_point_data = [];
        var hourly_days_labels = Object.keys(aggregated_hours);
        hourly_days_labels.sort();
        for (var hourly_key_index = 0; hourly_key_index < hourly_days_labels.length; hourly_key_index++) {
            var hourly_key = hourly_days_labels[hourly_key_index];
            hourly_chart_label_data.push(hourly_key);
            hourly_chart_point_data.push(aggregated_hours[hourly_key]);
        }

        var hourly_chart_data = {
            labels: hourly_chart_label_data,
            datasets: [{
                label: 'Time',
                type: "bar",
                backgroundColor: '#c39bd3',
                data: hourly_chart_point_data
            }]
        };

        //
        // weekly bar chart data
        //
        var weekly_chart_data = {
            labels: chart_labels, //['Mon 00', 'Mon 01', 'Mon 02', 'Mon 03', 'Mon 04', 'Mon 05', 'Mon 06'],
            datasets: [{
                label: 'Time',
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

        function y_axis_labels(value, index, values) {
            if (Math.floor(value / 10) === (value / 10)) {
                return seconds2time(value);
            }
        }

        function tooltip_labels(tooltipItem, data) {
            var label = data.datasets[tooltipItem.datasetIndex].label || '';

            if (label) {
                label += ": " + seconds2time(tooltipItem.yLabel);
            }

            return label;
        }

        //
        // daily chart
        //
        var daily_chart_canvas = view.querySelector('#daily_usage_chart_canvas');
        var ctx_daily = daily_chart_canvas.getContext('2d');

        if (daily_bar_chart) {
            console.log("destroy() existing chart: daily_bar_chart");
            daily_bar_chart.destroy();
        }

        daily_bar_chart = new Chart(ctx_daily, {
            type: 'bar',
            data: daily_chart_data,
            options: {
                legend: {
                    display: false
                },
                title: {
                    display: true,
                    text: "Usage by Day"
                },
                responsive: true,
                scales: {
                    xAxes: [{
                        stacked: false
                    }],
                    yAxes: [{
                        stacked: false,
                        ticks: {
                            autoSkip: true,
                            beginAtZero: true,
                            callback: y_axis_labels
                        }
                    }]
                },
                tooltips: {
                    mode: 'index',
                    intersect: false,
                    callbacks: {
                        label: tooltip_labels
                    }
                }
            }
        });

        //
        // hourly chart
        //
        var hourly_chart_canvas = view.querySelector('#hourly_usage_chart_canvas');
        var ctx_hourly = hourly_chart_canvas.getContext('2d');

        if (hourly_bar_chart) {
            console.log("destroy() existing chart: hourly_bar_chart");
            hourly_bar_chart.destroy();
        }

        hourly_bar_chart = new Chart(ctx_hourly, {
            type: 'bar',
            data: hourly_chart_data,
            options: {
                legend: {
                    display: false
                },
                title: {
                    display: true,
                    text: "Usage by Hour"
                },
                responsive: true,
                scales: {
                    xAxes: [{
                        stacked: false
                    }],
                    yAxes: [{
                        stacked: false,
                        ticks: {
                            autoSkip: true,
                            beginAtZero: true,
                            callback: y_axis_labels
                        }
                    }]
                },
                tooltips: {
                    mode: 'index',
                    intersect: false,
                    callbacks: {
                        label: tooltip_labels
                    }
                }
            }
        });

        //
        // weekly chart
        //
        var chart_canvas = view.querySelector('#weekly_usage_chart_canvas');
        var ctx_weekly = chart_canvas.getContext('2d');

        if (weekly_bar_chart) {
            console.log("destroy() existing chart: weekly_bar_chart");
            weekly_bar_chart.destroy();
        }

        weekly_bar_chart = new Chart(ctx_weekly, {
            type: 'bar',
            data: weekly_chart_data,
            options: {
                legend: {
                    display: false
                },
                title: {
                    display: true,
                    text: "Usage by Week"
                },
                responsive: true,
                scaleShowValues: true,
                scales: {
                    xAxes: [{
                        stacked: false,
                        ticks: {
                            //autoSkip: false
                        }
                    }],
                    yAxes: [{
                        stacked: false,
                        ticks: {
                            autoSkip: true,
                            beginAtZero: true,
                            callback: y_axis_labels
                        }
                    }]
                },
                tooltips: {
                    mode: 'index',
                    intersect: false,
                    callbacks: {
                        label: tooltip_labels
                    }
                }
            }
        });

        console.log("Charts Done");
    }

    function seconds2time(seconds) {
        var h = Math.floor(seconds / 3600);
        seconds = seconds - (h * 3600);
        var m = Math.floor(seconds / 60);
        var s = seconds - (m * 60);
        var time_string = padLeft(h) + ":" + padLeft(m) + ":" + padLeft(s);
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

            LibraryMenu.setTabs('playback_reporting', 3, getTabs);

            import(
                window.ApiClient.getUrl("web/ConfigurationPage", {
                  name: "Chart.bundle.min.js",
                })
            ).then((d3) => {

                var filter_url = window.ApiClient.getUrl("user_usage_stats/type_filter_list");
                console.log("loading types form : " + filter_url);
                window.ApiClient.getUserActivity(filter_url).then(function (filter_data) {
                    filter_names = filter_data;

                    // build filter list
                    var filter_items = "";
                    for (var x1 = 0; x1 < filter_names.length; x1++) {
                        var filter_name_01 = filter_names[x1];
                        filter_items += `
                        <label class="emby-checkbox-label" style="width: 6em;line-height: 39px;">
							<input type="checkbox" is="emby-checkbox" id='media_type_filter_"` + filter_name_01 + `' data_fileter_name='` + filter_name_01 + `' data-embycheckbox="true" checked class="emby-checkbox"> 
							<span class="checkboxLabel">` + filter_name_01 + `</span> 
							<span class="checkboxOutline">
								<span class="material-icons checkboxIcon checkboxIcon-checked check" aria-hidden="true"></span>
								<span class="material-icons checkboxIcon checkboxIcon-unchecked " aria-hidden="true"></span>
							</span>
						</label> `;
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

                        const timezoneOffset = -(new Date().getTimezoneOffset() / 60);
                        var url = "user_usage_stats/HourlyReport?days=" + days + "&endDate=" + end_date.value + "&filter=" + filter.join(",") + "&stamp=" + new Date().getTime() + "&timezoneOffset=" + timezoneOffset;
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
