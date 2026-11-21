const fs = require("fs");
const vm = require("vm");
const assert = require("assert");
const source = fs.readFileSync(__dirname + "/planner.js", "utf8") + "\n" +
  fs.readFileSync(__dirname + "/planner-ui.js", "utf8").split("let data;try")[0] +
  "\nthis.api={makeSeed,migrate,findMenu,findItem};";
const context = {};
vm.runInNewContext(source, context);
const {makeSeed,migrate,findMenu} = context.api;
const old = makeSeed();
const camera = old.groups.find(g=>g.key==="T");
const modes = old.groups.find(g=>g.key==="L");
const cameraMain = camera.menus.find(m=>m.name==="Главное меню");
const followIndex = cameraMain.items.findIndex(i=>i.label==="Настройки следования");
modes.menus[0].items.push(cameraMain.items.splice(followIndex,1)[0]);
const updated = migrate(old);
assert.equal(updated.schemaVersion,2);
const follow = updated.groups.find(g=>g.key==="L").menus[0].items.find(i=>i.label==="Настройки следования");
assert.equal(follow.type,"submenu");
assert.equal(findMenu(updated,follow.targetMenuId).g.key,"L");
assert.equal(updated.groups.find(g=>g.key==="T").menus.find(m=>m.name==="Главное меню").items.find(i=>i.label==="Запустить рандом").type,"submenu");
assert.equal(updated.groups.find(g=>g.key==="O").menus[0].items.find(i=>i.label==="Включить акулу").type,"toggle");
assert.equal(updated.groups.find(g=>g.key==="U").menus[0].items.find(i=>i.label==="Радиус загрузки").type,"list");
const count = updated.groups.reduce((n,g)=>n+g.menus.length,0);
assert.equal(migrate(updated).groups.reduce((n,g)=>n+g.menus.length,0),count);
console.log("Planner migration OK:",updated.groups.length,"keys,",count,"menus.");
if(process.argv[2]){
  const user=migrate(JSON.parse(fs.readFileSync(process.argv[2],"utf8")));
  const l=user.groups.find(g=>g.key==="L"),u=user.groups.find(g=>g.key==="U");
  assert(l.menus.some(m=>m.items.some(i=>i.label==="Крокодил — разрешить вызов Mouse5")));
  assert(l.menus.some(m=>m.items.some(i=>i.label==="Настройки следования")));
  assert(l.menus.some(m=>m.name==="Настройки следования"));
  assert(u.menus.some(m=>m.items.some(i=>i.label==="Включить очистку")));
  console.log("Saved user plan preserved:",l.menus.length,"L menus,",u.menus.length,"U menus.");
}
