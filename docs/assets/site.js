// Snaplume website: screenshot carousel tabs. Without JavaScript the carousel still scrolls horizontally.
(function () {
  document.documentElement.classList.add('js');

  var carousel = document.querySelector('.carousel');
  var tabs = Array.prototype.slice.call(document.querySelectorAll('.tabs [role="tab"]'));
  if (!carousel || !tabs.length) return;
  var slides = Array.prototype.slice.call(carousel.children);
  var reduce = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  function select(index, scroll) {
    tabs.forEach(function (tab, i) {
      var on = i === index;
      tab.setAttribute('aria-selected', on ? 'true' : 'false');
      tab.tabIndex = on ? 0 : -1;
    });
    if (scroll && slides[index]) {
      carousel.scrollTo({ left: slides[index].offsetLeft - carousel.offsetLeft, behavior: reduce ? 'auto' : 'smooth' });
    }
  }

  tabs.forEach(function (tab, i) {
    tab.addEventListener('click', function () { select(i, true); });
    tab.addEventListener('keydown', function (e) {
      var next = null;
      if (e.key === 'ArrowRight') next = (i + 1) % tabs.length;
      else if (e.key === 'ArrowLeft') next = (i - 1 + tabs.length) % tabs.length;
      else if (e.key === 'Home') next = 0;
      else if (e.key === 'End') next = tabs.length - 1;
      if (next === null) return;
      e.preventDefault();
      tabs[next].focus();
      select(next, true);
    });
  });

  // Keep the tabs in sync when the carousel is swiped.
  var timer;
  carousel.addEventListener('scroll', function () {
    clearTimeout(timer);
    timer = setTimeout(function () {
      var index = Math.round(carousel.scrollLeft / carousel.clientWidth);
      select(Math.max(0, Math.min(slides.length - 1, index)), false);
    }, 80);
  }, { passive: true });
})();
