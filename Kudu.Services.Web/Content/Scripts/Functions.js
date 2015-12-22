$(document).ready(function () {
    $(document).on("click", "#addFunction", function () {
        // show the new stuff
        console.log("add new function");
    });

    $(document).on("click", "#edit-files-tab", function () {
        if (!viewModel.editFunctionTab()) {
            viewModel.editFunctionTab(true);
            var originalHeight = $("#editor").height();
            $("#editor").css({ "height": originalHeight * 2 });
            editor.resize();
            viewModel.editFile(viewModel.currentFileObject());
        }
    });


    $(document).on("click", "#run-function-tab", function () {
        if (viewModel.editFunctionTab()) {
            viewModel.editFunctionTab(false);
            var originalHeight = $("#editor").height();
            $("#editor").css({ "height": originalHeight / 2 });
            editor.resize();
            viewModel.editSampleData();
        }
    });


    var functionsViewModel = function () {
        // Data
        var that = this;
        that.hostConfig = ko.observable();
        that.functions = ko.observableArray();
        that.files = ko.observableArray();
        that.editText = ko.observable("");
        that.newFileName = ko.observable("");
        that.currentFileObject = ko.observable();
        that.currentFunctionObject = ko.observable();
        that.newFunctionName = ko.observable();
        that.runId = ko.observable("");
        that.sampleDataText = ko.observable("");
        that.statusResponse = ko.observable();
        that.editFunctionTab = ko.observable(true);
        that.templates = ko.observableArray();
        that.creating = ko.observable(false);

        that.editHostConfig = function () {
            that.files([{
                name: "host.json",
                href: appRoot + "api/vfs/site/wwwroot/App_Data/jobs/functions/host.json"
            }]);
            that.currentFileObject(that.files()[0]);
            editor.setSyntaxForFunction(that.files()[0].name);
            that.editText(JSON.stringify(that.hostConfig(), undefined, 4));
        };

        that.saveFile = function () {
            var file;
            if (that.newFile) {
                file = {
                    name: that.newFileName(),
                    href: that.currentFunctionObject().script_root_path_href + "/" + that.newFileName()
                };
            } else {
                file = that.currentFileObject();
            }
            that.newFileName("");
            $.ajax({
                type: "PUT",
                url: file.href,
                data: that.editText(),
                headers: {
                    "If-Match": "*"
                },
                success: function () {
                    that.editFunction(that.currentFunctionObject(), file);
                }
            });
        };

        that.editFile = function (f) {
            that.newFile = false;
            that.currentFileObject(f);
            editor.setSyntaxForFunction(f.name);
            $.ajax({
                type: "GET",
                url: f.href,
                dataType: "text",
                success: function (data) {
                    that.editText(data);
                }
            });
        };

        that.editFunction = function (f, ef) {
            $("#edit-files-tab").click();
            that.currentFunctionObject(f);
            $.ajax({
                type: "GET",
                url: f.script_root_path_href,
                dataType: "json",
                success: function (data) {
                    that.files(data.filter(function (e) { return e.mime != "inode/directory"; }));
                    if (that.files().length > 0) {
                        if (ef && ef.name && files.filter(function (e) { return e.name === ef.name }).length > 0) {
                            that.editFile(ef);
                        } else {
                            that.editFile(that.files()[0]);
                        }
                    }
                }
            });
        }

        that.getHostConfig = function () {
            $.ajax({
                type: "GET",
                url: appRoot + "api/functions/config",
                dataType: "json",
                success: function (data) {
                    that.hostConfig(data);
                }
            });
        };

        that.getFunctions = function () {
            $.ajax({
                type: "GET",
                url: appRoot + "api/functions",
                dataType: "json",
                success: function (data) {
                    that.functions(data);
                }
            });
        };

        that.addingNewFile = function () {
            that.editText("");
            that.newFile = true;
        };

        that.createNewFunction = function () {
            if (that.newFunctionName()) {
                that.creating(true);
                $.ajax({
                    type: "PUT",
                    url: appRoot + "api/functions/" + that.newFunctionName(),
                    dataType: "json",
                    data: "",
                    success: function () {
                        that.getHostConfig();
                        that.getFunctions();
                        that.newFunctionName(undefined);
                    },
                    complete: function () {
                        that.creating(false);
                    }
                });
            }
        };

        that.editSampleData = function () {
            $.ajax({
                type: "GET",
                url: that.currentFunctionObject().test_data_href,
                dataType: "text",
                success: function (data) {
                    editor.setSyntaxForFunction("sample.dat");
                    that.editText(data);
                },
                error: function () {
                    editor.setSyntaxForFunction("sample.dat");
                    that.editText("");
                }
            });
        };

        that.runFunction = function () {
            $.ajax({
                type: "PUT",
                url: that.currentFunctionObject().test_data_href,
                dataType: "text",
                headers: {
                    "If-Match": "*"
                },
                data: that.editText()
            });
            $.ajax({
                type: "POST",
                url: that.currentFunctionObject().href + "/run",
                dataType: "json",
                data: that.editText(),
                success: function (data) {
                    that.runId(data.id);
                },
                error: function (err) {
                    that.statusResponse(err);
                }
            });
        };

        that.getStatus = function () {
            $.ajax({
                type: "GET",
                url: that.currentFunctionObject().href + "/status/" + that.runId(),
                dataType: "json",
                success: function (data) {
                    that.statusResponse(data);
                },
                error: function (err) {
                    that.statusResponse(err);
                }
            });
        };

        that.getTemplates = function () {
            $.ajax({
                type: "GET",
                url: appRoot + "api/functions/templates",
                dataType: "json",
                success: function (data) {
                    that.templates(data);
                }
            });
        };

        that.selectTemplate = function (t) {
            that.newFunctionName(t.name);
        };

        that.getHostConfig();
        that.getFunctions();
        that.getTemplates();
    };
    var viewModel = new functionsViewModel();
    window.viewModel = viewModel;
    ko.applyBindings(viewModel)

    $("#editFunctionModal").on("hidden.bs.modal", function () {
        viewModel.getHostConfig();
        viewModel.getFunctions();
        viewModel.runId("");
        viewModel.statusResponse(undefined);
    })
});