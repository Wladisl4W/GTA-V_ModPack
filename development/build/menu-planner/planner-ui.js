"use strict";
const STORAGE = "modpack-menu-planner-v1";
const BACKUP = "modpack-menu-planner-v1-backup";
const COLORS = {white:"#f2f4f8",green:"#79dc95",blue:"#83c8fa",pink:"#eaa0f3",yellow:"#f1d778",red:"#fa8b8b"};
const KEY_COLORS = {F5:COLORS.blue,T:COLORS.blue,Mouse4:COLORS.pink,I:COLORS.green,Y:COLORS.pink,L:COLORS.yellow,O:COLORS.red,Mouse5:COLORS.red,U:COLORS.blue,";":COLORS.green,K:COLORS.yellow,H:COLORS.yellow,"Ctrl+F10":COLORS.white,"Фон":COLORS.white};
const TYPE_NAMES = {submenu:"Подменю",toggle:"Галочка",list:"Список",action:"Действие",info:"Справка"};
const LINK_NAMES = {
  Camera:{"Главное меню":{"Запустить рандом":"Случайная пролётка","Редактировать узлы":"Редактор узлов","Загрузить путь":"Сохранённые пути","Настройки камеры":"Настройки камеры","Настройки следования":"Настройки следования","Сбросить все камеры":"Подтверждение сброса"},"Сохранённые пути":{"Открыть сохранённый путь [шаблон]":"Сохранённый путь → действия"},"Сохранённый путь → действия":{"Удалить":"Подтверждение удаления пути"},"Редактор узлов":{"Открыть узел [шаблон]":"Узел → параметры"}},
  Paint:{"Главное меню":{"Открыть покраску машины":"Покраска машины","Открыть случайную покраску":"Случайная покраска","Открыть действия с миром":"Действия с миром","Открыть пресеты":"Пресеты","Открыть пассажиров мотоциклов":"Пассажиры мотоциклов"},"Случайная покраска":{"Открыть случайные цвета":"Случайные цвета","Открыть исключения из покраски":"Исключения из покраски"},"Случайные цвета":{"Основной цвет — палитра":"Основная палитра","Вторичный цвет — палитра":"Вторичная палитра"},"Действия с миром":{"Открыть обмен моделей":"Обмен моделей"},"Пресеты":{"Открыть пресет [шаблон]":"Пресет → действия"},"Пресет → действия":{"Редактировать пресет":"Редактор пресета","Удалить пресет":"Подтверждение удаления пресета"},"Редактор пресета":{"Основные цвета":"Цвета пресета — основные","Вторичные цвета":"Цвета пресета — вторичные","Исключения рандомайзера":"Исключения пресета","Модели для открытия дверей":"Модели дверей пресета","Модели пассажиров мотоциклов":"Пассажиры пресета"}},
  Animations:{"Главное меню":{"Выбранные педы":"Выбранные педы","Анимации":"Анимации → разделы"},"Анимации → разделы":{"Готовые действия":"Готовые действия","Все анимации":"Все анимации","Избранное":"Избранное"}}
};
const EXTRA_MENUS = {
  Camera:[["Подтверждение сброса","Да, сбросить|Отмена"],["Подтверждение удаления пути","Да, удалить|Отмена"]],
  Paint:[["Подтверждение удаления пресета","Да, удалить|Отмена"],["Цвета пресета — основные","Цвет участвует в пресете [шаблон]"],["Цвета пресета — вторичные","Цвет участвует в пресете [шаблон]"],["Исключения пресета","Добавить модель под прицелом|Убрать модель [шаблон]"],["Модели дверей пресета","Добавить модель под прицелом|Убрать модель [шаблон]"],["Пассажиры пресета","Добавить модель под прицелом|Добавить из общего списка|Убрать модель [шаблон]"]]
};
function uid(prefix){return prefix+Math.random().toString(36).slice(2,11);}
function makeSeed(){let n=0;return {schemaVersion:1,notes:"",groups:seed.map(([key,title,module,menus])=>({id:"g"+(++n),key,title,menus:menus.map(([name,raw])=>({id:"m"+(++n),name,module,items:raw.split("|").map(label=>({id:"i"+(++n),label,origin:key+" → "+title+" → "+name,module}))}))}))};}
function findGroup(data,id){return data.groups.find(g=>g.id===id);}
function findMenu(data,id){for(const g of data.groups){const m=g.menus.find(x=>x.id===id);if(m)return {g,m};}return null;}
function findItem(data,id){for(const g of data.groups)for(const m of g.menus){const index=m.items.findIndex(i=>i.id===id);if(index>=0)return {g,m,index,item:m.items[index]};}return null;}
function sourceMenu(item){return String(item.origin||"").split(" → ").slice(2).join(" → ");}
function linkedName(item){return LINK_NAMES[item.module]?.[sourceMenu(item)]?.[item.label]||"";}
function suggestedType(item){if(item.module==="Catalog"||item.module==="Crash")return "info";if(linkedName(item))return "submenu";
  if(/^(Включить |Акула кусает|Крокодил —|Следование камеры$|Выдать оружие$|Сбивать педов|Скользкое падение$|Красить всю машину$|Дым шин|Цвет участвует|Случайная покраска машин$|Случайные текстуры объектов$|Номер всем машинам$|Открыть двери выбранных моделей$|Посадить пассажиров на мотоциклы$|Одинаковый цвет кузова$)/.test(item.label))return "toggle";
  if(/^(Радиус |Частота |Лимит |Тип краски$|Скорость |Основной цвет$|Вторичный цвет$|Длительность$|Интерполяция$|Цвет маркера$|Поле зрения|Вид от игрока$|Макс\. длительность$|Гравитация$|Сила удара$)/.test(item.label))return item.label==="Вид от игрока"?"toggle":"list";
  return "action";}
function suggestedColor(item){if(/^(Воспроизвести путь|Расставить узлы|Загрузить и воспроизвести|Отменить удаление|Перезапустить загрузку)/.test(item.label))return COLORS.green;
  if(/Запустить рандом/.test(item.label))return COLORS.pink;
  if(/^(Редактировать узлы|Поиск)/.test(item.label))return COLORS.blue;
  if(/^(Удалить|Да, удалить|Да, сбросить|Сбросить все камеры|Остановить загрузку)/.test(item.label))return COLORS.red;
  if(/^(Сканировать заново|Сбросить поиск|Переименовать)/.test(item.label))return COLORS.yellow;
  return COLORS.white;}
function ensureExtras(data){for(const [module,defs] of Object.entries(EXTRA_MENUS)){const group=data.groups.find(g=>g.menus.some(m=>m.module===module));if(!group)continue;
    for(const [name,raw] of defs){if(data.groups.some(g=>g.menus.some(m=>m.module===module&&m.name===name)))continue;
      group.menus.push({id:uid("m"),name,module,items:raw.split("|").map(label=>({id:uid("i"),label,origin:group.key+" → "+group.title+" → "+name,module}))});}}}
function migrate(input){if(!input||![1,2].includes(input.schemaVersion)||!Array.isArray(input.groups)||input.groups.length>100)throw Error("Неизвестный формат карты");
  const data=JSON.parse(JSON.stringify(input));let total=0;const ids=new Set();for(const g of data.groups){if(typeof g.id!=="string"||typeof g.key!=="string"||typeof g.title!=="string"||!Array.isArray(g.menus)||ids.has(g.id))throw Error("Повреждена группа");ids.add(g.id);g.color=/^#[0-9a-fA-F]{6}$/.test(g.color||"")?g.color:(KEY_COLORS[g.key]||COLORS.blue);
    for(const m of g.menus){if(typeof m.id!=="string"||typeof m.name!=="string"||!Array.isArray(m.items)||ids.has(m.id))throw Error("Повреждено меню");ids.add(m.id);m.module=String(m.module||"Новый раздел");
      for(const i of m.items){if(typeof i.id!=="string"||typeof i.label!=="string"||typeof i.origin!=="string"||ids.has(i.id)||++total>5000)throw Error("Повреждён пункт");ids.add(i.id);i.module=String(i.module||m.module);i.type=TYPE_NAMES[i.type]?i.type:suggestedType(i);i.color=/^#[0-9a-fA-F]{6}$/.test(i.color||"")?i.color:suggestedColor(i);}}}
  ensureExtras(data);
  for(const g of data.groups)for(const m of g.menus)for(const i of m.items){if(i.type!=="submenu"||findMenu(data,i.targetMenuId))continue;const target=linkedName(i);if(target){const dest=data.groups.flatMap(group=>group.menus).find(menu=>menu.module===i.module&&menu.name===target);if(dest)i.targetMenuId=dest.id;}}
  // If a navigation item was moved in the old planner, its whole submenu follows it.
  if(input.schemaVersion===1)for(const g of [...data.groups])for(const m of [...g.menus])for(const i of [...m.items]){if(i.type!=="submenu"||!i.targetMenuId)continue;const target=findMenu(data,i.targetMenuId);if(target&&target.g!==g){target.g.menus.splice(target.g.menus.indexOf(target.m),1);g.menus.push(target.m);}}
  for(const g of data.groups){if(!findMenu(data,g.rootMenuId)||findMenu(data,g.rootMenuId).g!==g)g.rootMenuId=(g.menus.find(m=>m.name==="Главное меню")||g.menus[0])?.id||"";}
  data.schemaVersion=2;data.notes=String(data.notes||"");return data;}
let data;try{const raw=localStorage.getItem(STORAGE);if(raw){const parsed=JSON.parse(raw);if(parsed.schemaVersion===1&&!localStorage.getItem(BACKUP))localStorage.setItem(BACKUP,raw);data=migrate(parsed);}else data=migrate(makeSeed());}catch(e){data=migrate(makeSeed());}
let route=[];const app=document.getElementById("app"),dialog=document.getElementById("editor"),fields=document.getElementById("editorFields"),buttons=document.getElementById("editorButtons"),toast=document.getElementById("toast");let toastTimer;
function node(tag,cls,text){const x=document.createElement(tag);if(cls)x.className=cls;if(text!==undefined)x.textContent=text;return x;}
function button(label,fn,cls){const b=node("button",cls,label);b.type="button";b.onclick=fn;return b;}
function notify(text){toast.textContent=text;toast.style.display="block";clearTimeout(toastTimer);toastTimer=setTimeout(()=>toast.style.display="none",3000);}
function save(){try{localStorage.setItem(STORAGE,JSON.stringify(data));}catch{notify("Автосохранение недоступно. Скачай JSON.");}}
function current(){const id=route[route.length-1],found=findMenu(data,id);return found||null;}
function home(){route=[];render();}
function openGroup(g){const m=findMenu(data,g.rootMenuId)?.m||g.menus[0];if(!m){notify("В этом направлении пока нет меню. Создай его через редактирование клавиши.");return;}route=[m.id];render();}
function openMenu(id){if(!findMenu(data,id)){notify("Для этого перехода пока нет подменю. Добавь его в редакторе пункта.");return;}route.push(id);render();}
function path(){const bar=node("nav","crumbs");bar.append(button("Все клавиши",home));for(let k=0;k<route.length;k++){const r=findMenu(data,route[k]);if(!r)continue;bar.append(node("span",null,"›"),button(k===0?r.g.key+" · "+r.g.title:r.m.name,()=>{route=route.slice(0,k+1);render();}));}return bar;}
function countItems(g){return g.menus.reduce((n,m)=>n+m.items.length,0);}
function renderHome(){app.append(node("h2","pageTitle","Все клавиши"),node("p","subline","Открой направление, затем переходи по пунктам как в GTA. Цвет клавиши можно изменить через ✎."));const grid=node("div","grid");
  for(const g of data.groups){const card=node("div","keyCard");card.style.setProperty("--accent",g.color);const open=button("",()=>openGroup(g),"keyOpen");open.append(node("div","keyName",g.key),node("div","keyTitle",g.title),node("div","keyMeta",g.menus.length+" меню · "+countItems(g)+" пунктов"));card.append(open,button("✎",()=>editGroup(g),"editButton"));grid.append(card);}app.append(grid);}
function icon(i){return {submenu:"›",toggle:i.previewChecked?"☑":"☐",list:"◀",action:"●",info:"i"}[i.type]||"●";}
function renderMenu(){const found=current();if(!found){home();return;}const {g,m}=found;app.append(path());const head=node("h2","pageTitle",m.name);app.append(head,node("p","subline",g.key+" · "+g.title+" · источник: "+m.module));
  const box=node("section","gtaMenu");box.style.setProperty("--accent",g.color);const bh=node("div","gtaHead");bh.append(node("strong",null,m.name),node("small",null,g.title+" · "+g.key));box.append(bh);
  if(!m.items.length)box.append(node("div","empty","Здесь пока нет пунктов. Добавь действие или перенеси его из другого меню."));
  for(const i of m.items){const row=node("div","row"+(i.label.includes("[шаблон]")?" dynamic":""));row.style.setProperty("--item-color",i.color);const main=button("",()=>activate(i),"rowMain");main.append(node("span","rowSymbol",icon(i)),node("span","rowText",i.label.replace(" [шаблон]","")));main.append(node("span","rowType",TYPE_NAMES[i.type]));row.append(main,button("⋯ Переместить / изменить",()=>editItem(i),"editButton"));box.append(row);}app.append(box);
  const actions=node("div","menuActions");actions.append(button("+ Добавить пункт",()=>addItem(m)),button("+ Создать подменю",()=>addMenu(g,m)),button("Изменить это меню",()=>editMenu(g,m)));app.append(actions);
  const legend=node("div","legend");legend.innerHTML="<b>›</b> открыть подменю · <b>☐</b> галочка · <b>◀</b> выбор значения · <b>●</b> действие. Нажатия здесь только показывают поведение; игра не запускает функции.";app.append(legend);
  const notes=node("div","notes");notes.append(node("p",null,"Заметки к плану"));const text=node("textarea");text.placeholder="Например: I оставить только для машины под прицелом; пресеты вынести отдельно…";text.value=data.notes;text.oninput=()=>{data.notes=text.value;save();};notes.append(text);app.append(notes);}
function activate(i){if(i.type==="submenu"){if(i.targetMenuId)openMenu(i.targetMenuId);else notify("Укажи целевое подменю через кнопку изменения.");return;}if(i.type==="toggle"){i.previewChecked=!i.previewChecked;render();return;}if(i.type==="list"){notify("Это список значений в GTA. Настрой его тип и цвет через ⋯.");return;}notify(i.type==="info"?"Справочный пункт":"Макет действия: "+i.label+". В GTA ничего не изменилось.");}
function render(){app.replaceChildren();if(route.length)renderMenu();else renderHome();}
function input(label,value,type="text"){const l=node("label",null,label),x=node("input");x.type=type;x.value=value;l.append(x);fields.append(l);return x;}
function select(label,choices,value){const l=node("label",null,label),x=node("select");for(const [v,text] of choices){const o=node("option",null,text);o.value=v;if(v===value)o.selected=true;x.append(o);}l.append(x);fields.append(l);return x;}
function startEditor(title){document.getElementById("editorTitle").textContent=title;fields.replaceChildren();buttons.replaceChildren();buttons.append(button("Отмена",()=>dialog.close()));}
function showEditor(){dialog.showModal();}
function finish(){save();dialog.close();render();}
function allMenus(){return data.groups.flatMap(g=>g.menus.map(m=>({g,m})));}
function moveMenuTree(id,targetGroup,seen=new Set()){if(seen.has(id))return;seen.add(id);const found=findMenu(data,id);if(!found||found.g===targetGroup)return;const previous=found.g;previous.menus.splice(previous.menus.indexOf(found.m),1);if(previous.rootMenuId===id)previous.rootMenuId=(previous.menus.find(m=>m.name==="Главное меню")||previous.menus[0])?.id||"";targetGroup.menus.push(found.m);if(!targetGroup.rootMenuId)targetGroup.rootMenuId=id;for(const i of found.m.items)if(i.type==="submenu"&&i.targetMenuId)moveMenuTree(i.targetMenuId,targetGroup,seen);}
function editItem(i){const found=findItem(data,i.id);if(!found)return;startEditor("Пункт меню");fields.append(node("p",null,"Изначально: "+i.origin));const name=input("Название в GTA",i.label),color=input("Цвет текста",i.color,"color");const type=select("Тип пункта",Object.entries(TYPE_NAMES),i.type);
  const target=select("Куда ведёт подменю",[["","Пока не выбрано"],...allMenus().filter(x=>x.m.id!==found.m.id).map(x=>[x.m.id,x.g.key+" · "+x.g.title+" → "+x.m.name])],i.targetMenuId||"");
  const dest=select("Переместить в меню",allMenus().map(x=>[x.m.id,x.g.key+" · "+x.g.title+" → "+x.m.name]),found.m.id);
  const targetLabel=target.parentElement;const update=()=>targetLabel.classList.toggle("hidden",type.value!=="submenu");type.onchange=update;update();
  buttons.append(button("↑ Выше",()=>{const p=findItem(data,i.id);if(p.index>0){[p.m.items[p.index-1],p.m.items[p.index]]=[p.m.items[p.index],p.m.items[p.index-1]];finish();}}),button("↓ Ниже",()=>{const p=findItem(data,i.id);if(p.index<p.m.items.length-1){[p.m.items[p.index+1],p.m.items[p.index]]=[p.m.items[p.index],p.m.items[p.index+1]];finish();}}));
  buttons.append(button("Сохранить / переместить",()=>{if(!name.value.trim())return notify("Название не может быть пустым.");i.label=name.value.trim();i.color=color.value;i.type=type.value;i.targetMenuId=i.type==="submenu"?target.value:"";
    const newMenu=findMenu(data,dest.value);if(newMenu&&newMenu.m!==found.m){found.m.items.splice(found.index,1);newMenu.m.items.push(i);if(i.type==="submenu"&&i.targetMenuId)moveMenuTree(i.targetMenuId,newMenu.g);route=[newMenu.m.id];}
    finish();}));showEditor();}
function editGroup(g){startEditor("Клавиша / направление");const key=input("Клавиша или пометка",g.key),title=input("Название мода",g.title),color=input("Цвет карточки и заголовка",g.color,"color");
  const root=select("Какое меню открывается первым",g.menus.map(m=>[m.id,m.name]),g.rootMenuId||"");buttons.append(button("Сохранить",()=>{if(!key.value.trim()||!title.value.trim())return notify("Нужны клавиша и название.");g.key=key.value.trim();g.title=title.value.trim();g.color=color.value;g.rootMenuId=root.value||g.rootMenuId;finish();}));
  if(countItems(g)===0)buttons.prepend(button("Удалить пустую клавишу",()=>{if(data.groups.flatMap(x=>x.menus).some(m=>m.items.some(i=>g.menus.some(target=>target.id===i.targetMenuId))))return notify("На одно из меню ещё ведёт пункт. Сначала измени переход.");if(!confirm("Удалить "+g.key+" и все её пустые меню?"))return;data.groups.splice(data.groups.indexOf(g),1);route=[];finish();},"danger"));showEditor();}
function editMenu(g,m){startEditor("Меню");const name=input("Название меню",m.name),dest=select("Под какой клавишей",data.groups.map(group=>[group.id,group.key+" · "+group.title]),g.id);
  buttons.append(button("Сохранить / переместить",()=>{if(!name.value.trim())return notify("Название не может быть пустым.");m.name=name.value.trim();const newGroup=findGroup(data,dest.value);if(newGroup&&newGroup!==g)moveMenuTree(m.id,newGroup);route=[m.id];finish();}));
  if(!m.items.length)buttons.prepend(button("Удалить пустое меню",()=>{if(data.groups.flatMap(x=>x.menus).some(x=>x.items.some(i=>i.targetMenuId===m.id)))return notify("На это меню ещё ведёт пункт. Сначала измени его переход.");g.menus.splice(g.menus.indexOf(m),1);if(g.rootMenuId===m.id)g.rootMenuId=g.menus[0]?.id||"";route=[];finish();},"danger"));showEditor();}
function addMenu(g,parent){startEditor("Новое подменю");const name=input("Название","Новое меню");buttons.append(button("Создать",()=>{if(!name.value.trim())return notify("Нужно название.");const m={id:uid("m"),name:name.value.trim(),module:parent?.module||"Новый раздел",items:[]};g.menus.push(m);if(!g.rootMenuId)g.rootMenuId=m.id;
    if(parent){parent.items.push({id:uid("i"),label:m.name,origin:"Добавлено в плане",module:m.module,type:"submenu",color:COLORS.white,targetMenuId:m.id});}route=[m.id];finish();}));showEditor();}
function addItem(m){startEditor("Новый пункт");const name=input("Название","Новое действие"),type=select("Тип",Object.entries(TYPE_NAMES),"action");buttons.append(button("Добавить",()=>{if(!name.value.trim())return notify("Нужно название.");m.items.push({id:uid("i"),label:name.value.trim(),origin:"Добавлено в плане",module:m.module,type:type.value,color:COLORS.white});finish();}));showEditor();}
function download(name,text,mime){const url=URL.createObjectURL(new Blob([text],{type:mime})),a=node("a");a.href=url;a.download=name;document.body.append(a);a.click();a.remove();setTimeout(()=>URL.revokeObjectURL(url),1000);}
function markdown(){let s="# План игровых меню ModPack\n\n"+(data.notes?"## Пожелания\n\n"+data.notes+"\n\n":"");for(const g of data.groups){s+="## "+g.key+" — "+g.title+"\n\n";for(const m of g.menus){s+="### "+m.name+"\n\n";for(const i of m.items){const target=findMenu(data,i.targetMenuId);s+="- "+i.label+" — "+TYPE_NAMES[i.type]+(target?" → "+target.m.name:"")+"; сейчас: "+i.origin+"\n";}s+="\n";}}return s;}
document.getElementById("home").onclick=home;
document.getElementById("newGroup").onclick=()=>{startEditor("Новая клавиша / направление");const key=input("Клавиша или 'пока не решил'",""),title=input("Название",""),color=input("Цвет",COLORS.blue,"color");buttons.append(button("Создать",()=>{if(!key.value.trim()||!title.value.trim())return notify("Нужны клавиша и название.");const g={id:uid("g"),key:key.value.trim(),title:title.value.trim(),color:color.value,menus:[],rootMenuId:""};data.groups.push(g);finish();addMenu(g,null);}));showEditor();};
document.getElementById("exportJson").onclick=()=>{save();download("modpack-menu-plan.json",JSON.stringify(data,null,2),"application/json;charset=utf-8");};
document.getElementById("exportMd").onclick=()=>download("modpack-menu-plan.md",markdown(),"text/markdown;charset=utf-8");
document.getElementById("importJson").onchange=async e=>{const file=e.target.files[0];if(!file)return;try{const imported=migrate(JSON.parse(await file.text()));data=imported;route=[];save();render();notify("План открыт.");}catch(err){alert("Не удалось открыть план: "+err.message);}e.target.value="";};
document.getElementById("reset").onclick=()=>{if(!confirm("Вернуть исходную карту? Сначала скачай свой план в JSON."))return;data=migrate(makeSeed());route=[];save();render();};
window.ModPackPlanner={migrate,makeSeed,getData:()=>data};
save();render();
