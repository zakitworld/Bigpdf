// Navbar dropdown toggle logic
// Toggles the .is-open class on the parent .nav-dropdown and closes any other open dropdowns.
// Also listens for clicks outside to auto-close, and Escape key to dismiss.

function toggleDropdown(trigger) {
    const dropdown = trigger.closest('.nav-dropdown');
    const isOpen = dropdown.classList.contains('is-open');

    // Close all open dropdowns first
    document.querySelectorAll('.nav-dropdown.is-open').forEach(function (d) {
        d.classList.remove('is-open');
    });

    // If it was closed, open it
    if (!isOpen) {
        dropdown.classList.add('is-open');
    }
}

// Close dropdowns when clicking outside of them
document.addEventListener('click', function (e) {
    if (!e.target.closest('.nav-dropdown')) {
        document.querySelectorAll('.nav-dropdown.is-open').forEach(function (d) {
            d.classList.remove('is-open');
        });
    }
});

// Close dropdowns on Escape
document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') {
        document.querySelectorAll('.nav-dropdown.is-open').forEach(function (d) {
            d.classList.remove('is-open');
        });
        // Also close mobile nav
        document.querySelectorAll('.site-header.nav-open').forEach(function (h) {
            h.classList.remove('nav-open');
        });
    }
});
