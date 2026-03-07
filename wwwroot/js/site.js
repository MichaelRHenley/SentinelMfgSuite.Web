// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.

document.addEventListener("DOMContentLoaded", function () {
    // Only run on the Launch List page
    var form = document.getElementById("launchListForm");
    var nameInput = document.getElementById("LaunchName");
    var emailInput = document.getElementById("LaunchEmail");
    var btn = document.getElementById("joinLaunchListBtn");

    if (!form || !nameInput || !emailInput || !btn) {
        // Not the launch list page, bail out
        return;
    }

    function isEmail(value) {
        value = value.trim();
        if (value.length < 5) return false;

        var at = value.indexOf("@");
        if (at < 1) return false;

        var dot = value.lastIndexOf(".");
        if (dot <= at + 1) return false;
        if (dot === value.length - 1) return false;

        return true;
    }

    function checkForm() {
        var nameVal = nameInput.value.trim();
        var emailVal = emailInput.value.trim();

        var ok = true;

        if (!nameVal) ok = false;
        if (!emailVal || !isEmail(emailVal)) ok = false;

        btn.disabled = !ok;
        if (btn.disabled) {
            btn.classList.add("btn-sentinel-primary--disabled");
        } else {
            btn.classList.remove("btn-sentinel-primary--disabled");
        }
    }

    nameInput.addEventListener("input", checkForm);
    emailInput.addEventListener("input", checkForm);
    nameInput.addEventListener("change", checkForm);
    emailInput.addEventListener("change", checkForm);

    // initial state
    checkForm();
});
