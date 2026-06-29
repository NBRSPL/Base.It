function setMode(mode){
  var sub = mode === 'sub';
  document.getElementById('tab-sub').classList.toggle('on', sub);
  document.getElementById('tab-perp').classList.toggle('on', !sub);
  document.getElementById('billnote').textContent = sub
    ? 'Billed annually · per developer seat · 30-day money-back guarantee'
    : 'One-time purchase · per developer seat · includes 1 year of updates';
  document.querySelectorAll('[data-sub]').forEach(function(el){
    el.innerHTML = sub ? el.getAttribute('data-sub') : el.getAttribute('data-perp');
  });
}
