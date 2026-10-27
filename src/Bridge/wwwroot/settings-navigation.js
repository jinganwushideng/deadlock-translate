/* One scroller, one cancellable animation. Selection never routes back into scrolling. */
(function(){
 'use strict';var content=document.getElementById('contentScroll'),active='overview',token=0,frame=0,moving=false,destination=0,follow=false,observer=0;
 var reduced=window.matchMedia('(prefers-reduced-motion: reduce)');if(!content)return;
 function cancel(user){token++;if(frame)cancelAnimationFrame(frame);frame=0;moving=false;follow=!!user;}
 function group(id){var target=document.getElementById(id);if(!target||target.closest('section.page').id!==active)return;
  document.querySelectorAll('section.page>article.card').forEach(function(card){card.classList.toggle('is-current-group',card.id===id);});
  document.querySelectorAll('.subnav button').forEach(function(b){b.classList.toggle('current',b.dataset.section===id);b.setAttribute('aria-current',b.dataset.section===id?'location':'false');});
 }
 function locate(top){var from=content.scrollTop,to=Math.max(0,Math.min(content.scrollHeight-content.clientHeight,top));destination=to;
  if(reduced.matches||Math.abs(to-from)<=6){content.scrollTop=to;return;}
  var own=token,start=performance.now(),distance=to-from,duration=Math.min(650,220+Math.sqrt(Math.abs(distance))*9),height=content.scrollHeight,viewport=content.clientHeight;moving=true;
  function tick(now){if(own!==token)return;if(content.scrollHeight!==height||content.clientHeight!==viewport){cancel(false);return;}var progress=Math.min(1,Math.max(0,(now-start)/duration));content.scrollTop=from+distance*(1-Math.cos(Math.PI*progress))/2;if(progress<1)frame=requestAnimationFrame(tick);else{frame=0;moving=false;}}
  frame=requestAnimationFrame(tick);
 }
 function activate(page,section){var node=document.getElementById(page),target=section?document.getElementById(section):null;if(!node||!node.matches('section.page')||target&&target.closest('section.page')!==node)return;
  cancel(false);var changed=page!==active;document.querySelectorAll('section.page').forEach(function(p){p.hidden=p!==node;});if(changed)content.scrollTop=0;active=page;
  document.querySelectorAll('nav [data-page]').forEach(function(b){var selected=b.dataset.page===page;b.classList.toggle('active',selected);b.parentElement.classList.toggle('is-active',selected);b.setAttribute('aria-expanded',String(selected));});
  var first=node.querySelector(':scope>article.card');group(section||first&&first.id);locate(target?target.getBoundingClientRect().top-content.getBoundingClientRect().top+content.scrollTop-14:0);
 }
 ['wheel','touchstart','pointerdown'].forEach(function(name){content.addEventListener(name,function(){cancel(true);},{passive:true});});
 content.addEventListener('scroll',function(){if(!follow||moving||observer)return;observer=requestAnimationFrame(function(){observer=0;if(!follow||moving)return;var cards=Array.from(document.getElementById(active).querySelectorAll(':scope>article.card')),line=content.getBoundingClientRect().top+60;var card=cards.find(function(c){return c.getBoundingClientRect().bottom>line;})||cards[cards.length-1];if(card)group(card.id);});},{passive:true});
 document.addEventListener('keydown',function(e){if(['PageUp','PageDown','Home','End','ArrowUp','ArrowDown',' '].indexOf(e.key)>=0&&(content.contains(e.target)||e.target.closest&&e.target.closest('nav')))cancel(true);});
 window.addEventListener('resize',function(){cancel(false);});
 var onReduced=function(e){if(e.matches&&moving){var to=destination;cancel(false);content.scrollTop=to;}};if(reduced.addEventListener)reduced.addEventListener('change',onReduced);else if(reduced.addListener)reduced.addListener(onReduced);
 window.DltSettingsNavigation={activate:activate,cancel:function(){cancel(false);},locateElement:function(node){cancel(false);locate(node.getBoundingClientRect().top-content.getBoundingClientRect().top+content.scrollTop-content.clientHeight/2);},get moving(){return moving;},get page(){return active;}};
}());
