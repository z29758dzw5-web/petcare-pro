
(function(){
  var commercialCustomers=[];
  var commercialMembers=[];
  var tableState={};
  var loadingCount=0;

  function ensureLoader(){
    if(document.getElementById('pcLoader')) return;
    var el=document.createElement('div');
    el.id='pcLoader';
    el.className='loader-bg hidden';
    el.innerHTML='<div class="loader-card"><span class="spinner"></span><span>正在处理，请稍候…</span></div>';
    document.body.appendChild(el);
  }
  function setLoading(on){
    ensureLoader();
    loadingCount=Math.max(0,loadingCount+(on?1:-1));
    document.getElementById('pcLoader').classList.toggle('hidden',loadingCount===0);
  }

  var oldApi=api;
  api=async function(url,opt){
    setLoading(true);
    try{return await oldApi(url,opt||{})}
    finally{setLoading(false)}
  };

  function addNav(){
    var wanted=[['appointment','预约管理'],['member','会员管理'],['checkout','收银结算'],['logs','操作日志']];
    wanted.forEach(function(x){
      if(!navItems.some(function(n){return n[0]===x[0]})){
        if(x[0]==='appointment') navItems.splice(1,0,x);
        else if(x[0]==='member') navItems.splice(2,0,x);
        else if(x[0]==='checkout') navItems.splice(3,0,x);
        else navItems.push(x);
      }
    });
    try{renderNav()}catch(e){}
  }

  function statusHtml(v){
    var x=String(v==null?'':v);
    var cls='gray';
    if(['已支付','已完成','正常','已确认','进行中','寄养中','金卡会员'].indexOf(x)>=0) cls='green';
    else if(['待确认','预约中','普通会员','待完成','银卡会员'].indexOf(x)>=0) cls='blue';
    else if(['待支付','待处理','暂停'].indexOf(x)>=0) cls='orange';
    else if(['已取消','退款','异常','停用'].indexOf(x)>=0) cls='red';
    return '<span class="status '+cls+'">'+esc(x||'-')+'</span>';
  }
  function renderCell(row,key){
    if(key==='status'||key==='paymentStatus'||key==='level') return statusHtml(row[key]);
    if(key==='price'||key==='balance') return '¥ '+Number(row[key]||0).toFixed(2);
    return esc(row[key]==null?'':row[key]);
  }
  function rowActions(kind,x){
    var data=JSON.stringify(x).replace(/'/g,'&#39;');
    if(kind==='logs') return '';
    if(kind==='appointment') return '<button class="btn secondary" onclick=\'editRow("appointment",'+data+')\'>编辑</button><button class="btn danger" onclick="removeRow(\'appointment\',\''+esc(x.id)+'\')">删除</button>';
    if(kind==='member') return '<button class="btn secondary" onclick=\'editRow("member",'+data+')\'>编辑</button><button class="btn primary" onclick=\'rechargeMember('+data+')\'>充值</button><button class="btn danger" onclick="removeRow(\'member\',\''+esc(x.id)+'\')">删除</button>';
    if(kind==='checkout') return x.paymentStatus==='已支付'?'<span class="muted">'+esc(x.paymentMethod||'已结算')+'</span>':'<button class="btn primary" onclick=\'openCheckout('+data+')\'>收款</button>';
    return '<button class="btn secondary" onclick=\'editRow("'+kind+'",'+data+')\'>编辑</button><button class="btn danger" onclick="removeRow(\''+kind+'\',\''+esc(x.id)+'\')">删除</button>';
  }

  drawTable=function(list,cols,kind){
    tableState[kind]={list:list.slice(),cols:cols,page:1,pageSize:8};
    renderTablePage(kind);
  };
  window.changePage=function(kind,delta){
    if(!tableState[kind]) return;
    tableState[kind].page+=delta;
    renderTablePage(kind);
  };
  function renderTablePage(kind){
    var st=tableState[kind],wrap=document.getElementById('tableWrap');
    if(!st||!wrap) return;
    var total=st.list.length,pages=Math.max(1,Math.ceil(total/st.pageSize));
    st.page=Math.max(1,Math.min(st.page,pages));
    var rows=st.list.slice((st.page-1)*st.pageSize,st.page*st.pageSize);
    if(!total){wrap.innerHTML='<div class="empty">📭 暂无符合条件的数据</div>';return}
    var h='<table><thead><tr>';
    st.cols.forEach(function(c){h+='<th>'+c[1]+'</th>'});
    if(kind!=='logs') h+='<th>操作</th>';
    h+='</tr></thead><tbody>';
    rows.forEach(function(x){
      h+='<tr>';
      st.cols.forEach(function(c){h+='<td>'+renderCell(x,c[0])+'</td>'});
      if(kind!=='logs') h+='<td class="actions">'+rowActions(kind,x)+'</td>';
      h+='</tr>';
    });
    h+='</tbody></table><div class="pager"><span class="info">共 '+total+' 条 · 第 '+st.page+'/'+pages+' 页</span>';
    h+='<button class="btn secondary" onclick="changePage(\''+kind+'\',-1)" '+(st.page<=1?'disabled':'')+'>上一页</button>';
    h+='<button class="btn secondary" onclick="changePage(\''+kind+'\',1)" '+(st.page>=pages?'disabled':'')+'>下一页</button></div>';
    wrap.innerHTML=h;
  }

  window.currentRows=function(kind){return tableState[kind]?tableState[kind].list:[]};
  window.exportExcel=function(filename,rows,cols){
    if(!rows||!rows.length){alert('当前没有可导出的数据');return}
    var h='<html><head><meta charset="utf-8"></head><body><table border="1"><thead><tr>';
    cols.forEach(function(c){h+='<th>'+esc(c[1])+'</th>'});
    h+='</tr></thead><tbody>';
    rows.forEach(function(r){h+='<tr>';cols.forEach(function(c){h+='<td>'+esc(r[c[0]]==null?'':r[c[0]])+'</td>'});h+='</tr>'});
    h+='</tbody></table></body></html>';
    var blob=new Blob(['\ufeff',h],{type:'application/vnd.ms-excel'});
    var a=document.createElement('a');a.href=URL.createObjectURL(blob);a.download=filename+'.xls';document.body.appendChild(a);a.click();a.remove();setTimeout(function(){URL.revokeObjectURL(a.href)},1000);
  };

  var baseGo=go;
  go=async function(k){
    current=k;side.classList.remove('open');renderNav();
    if(k==='appointment'){await refreshPets();commercialCustomers=await api('/api/customers');cache=await api('/api/appointments');renderAppointmentTable();return}
    if(k==='member'){commercialCustomers=await api('/api/customers');cache=await api('/api/members');commercialMembers=cache.slice();renderMemberTable();return}
    if(k==='checkout'){commercialMembers=await api('/api/members');cache=await api('/api/checkout/orders');renderCheckoutTable();return}
    if(k==='logs'){cache=await api('/api/logs');renderLogTable();return}
    return baseGo(k);
  };

  function renderAppointmentTable(){
    pageTitle.textContent='预约管理';pageSub.textContent='管理客户预约、时间、员工和服务状态';
    var cols=[['id','预约编号'],['customerName','客户'],['petName','宠物'],['serviceType','服务项目'],['appointmentTime','预约时间'],['staff','员工'],['status','状态']];
    content.innerHTML='<div class="panel"><div class="toolbar"><div class="commercial-tools"><input id="appointmentSearch" class="search" placeholder="搜索客户 / 宠物 / 服务 / 员工" oninput="filterAppointment()"><select id="appointmentStatus" onchange="filterAppointment()"><option value="">全部状态</option><option>待确认</option><option>已确认</option><option>进行中</option><option>已完成</option><option>已取消</option></select><button class="btn primary" onclick="openAppointment()">＋ 新增预约</button><button id="exportAppointment" class="btn secondary">导出 Excel</button></div></div><div id="tableWrap"></div></div>';
    document.getElementById('exportAppointment').onclick=function(){exportExcel('预约管理',currentRows('appointment'),cols)};
    drawTable(cache,cols,'appointment');
  }
  window.filterAppointment=function(){
    var q=document.getElementById('appointmentSearch').value.toLowerCase(),st=document.getElementById('appointmentStatus').value;
    var rows=cache.filter(function(x){return (!q||JSON.stringify(x).toLowerCase().indexOf(q)>=0)&&(!st||x.status===st)});
    drawTable(rows,[['id','预约编号'],['customerName','客户'],['petName','宠物'],['serviceType','服务项目'],['appointmentTime','预约时间'],['staff','员工'],['status','状态']],'appointment');
  };
  window.openAppointment=function(data){
    data=data||null;modal.classList.remove('hidden');modalTitle.textContent=data?'编辑预约':'新增预约';modalSub.textContent='预约编号会自动生成';
    var customer='<div class="field"><label>客户</label><select data-k="customerId"><option value="">请选择客户</option>';
    commercialCustomers.forEach(function(c){customer+='<option value="'+esc(c.id)+'" '+(data&&data.customerId===c.id?'selected':'')+'>'+esc(c.name)+'（'+esc(c.id)+'）</option>'});
    customer+='</select></div>';
    var status='<div class="field"><label>状态</label><select data-k="status">';
    ['待确认','已确认','进行中','已完成','已取消'].forEach(function(x){status+='<option '+(data&&data.status===x?'selected':'')+'>'+x+'</option>'});
    status+='</select></div>';
    modalBody.innerHTML='<div class="grid2">'+customer+'<div class="field"><label>宠物</label>'+petSelect(data?data.petId:'')+'</div>'+fld('服务项目','serviceType',data?data.serviceType:'','text','例如：基础洗护')+fld('预约时间','appointmentTime',data?data.appointmentTime:'','datetime-local')+fld('服务员工','staff',data?data.staff:'','text','例如：小王')+status+fld('备注','remark',data?data.remark:'','text','可填写特殊要求')+'</div>';
    modalSave.textContent='保存';
    modalSave.onclick=async function(){var d=getData();await api(data?'/api/appointments/'+encodeURIComponent(data.id):'/api/appointments',{method:data?'PUT':'POST',body:JSON.stringify(d)});closeModal();await go('appointment')};
  };

  function renderMemberTable(){
    pageTitle.textContent='会员管理';pageSub.textContent='管理会员等级、积分、余额和状态';
    var cols=[['id','会员编号'],['customerName','客户'],['phone','手机号'],['level','等级'],['points','积分'],['balance','余额'],['status','状态'],['joinDate','加入日期']];
    content.innerHTML='<div class="panel"><div class="toolbar"><div class="commercial-tools"><input id="memberSearch" class="search" placeholder="搜索会员 / 客户 / 手机号" oninput="filterMember()"><select id="memberLevel" onchange="filterMember()"><option value="">全部等级</option><option>普通会员</option><option>银卡会员</option><option>金卡会员</option></select><button class="btn primary" onclick="openMember()">＋ 新增会员</button><button id="exportMember" class="btn secondary">导出 Excel</button></div></div><div id="tableWrap"></div></div>';
    document.getElementById('exportMember').onclick=function(){exportExcel('会员管理',currentRows('member'),cols)};
    drawTable(cache,cols,'member');
  }
  window.filterMember=function(){
    var q=document.getElementById('memberSearch').value.toLowerCase(),lv=document.getElementById('memberLevel').value;
    var rows=cache.filter(function(x){return (!q||JSON.stringify(x).toLowerCase().indexOf(q)>=0)&&(!lv||x.level===lv)});
    drawTable(rows,[['id','会员编号'],['customerName','客户'],['phone','手机号'],['level','等级'],['points','积分'],['balance','余额'],['status','状态'],['joinDate','加入日期']],'member');
  };
  window.openMember=function(data){
    data=data||null;modal.classList.remove('hidden');modalTitle.textContent=data?'编辑会员':'新增会员';modalSub.textContent='会员编号会自动生成';
    var customer='<div class="field"><label>客户</label><select data-k="customerId" '+(data?'disabled':'')+'><option value="">请选择客户</option>';
    commercialCustomers.forEach(function(c){customer+='<option value="'+esc(c.id)+'" '+(data&&data.customerId===c.id?'selected':'')+'>'+esc(c.name)+'（'+esc(c.id)+'）</option>'});
    customer+='</select></div>';
    var level='<div class="field"><label>会员等级</label><select data-k="level">';
    ['普通会员','银卡会员','金卡会员'].forEach(function(x){level+='<option '+(data&&data.level===x?'selected':'')+'>'+x+'</option>'});
    level+='</select></div>';
    var st='<div class="field"><label>状态</label><select data-k="status"><option '+(data&&data.status==='正常'?'selected':'')+'>正常</option><option '+(data&&data.status==='停用'?'selected':'')+'>停用</option></select></div>';
    modalBody.innerHTML='<div class="grid2">'+customer+level+fld('积分','points',data?data.points:0,'number')+fld('余额','balance',data?data.balance:0,'number')+st+'</div>';
    modalSave.textContent='保存';
    modalSave.onclick=async function(){var d=getData();d.points=Number(d.points||0);d.balance=Number(d.balance||0);await api(data?'/api/members/'+encodeURIComponent(data.id):'/api/members',{method:data?'PUT':'POST',body:JSON.stringify(d)});closeModal();await go('member')};
  };
  window.rechargeMember=async function(data){
    var amount=prompt('给 '+(data.customerName||data.id)+' 充值金额：');if(amount===null)return;
    var n=Number(amount);if(!n||n<=0){alert('请输入正确的充值金额');return}
    await api('/api/members/'+encodeURIComponent(data.id)+'/recharge',{method:'POST',body:JSON.stringify({amount:n})});await go('member');
  };

  function renderCheckoutTable(){
    pageTitle.textContent='收银结算';pageSub.textContent='查看待支付订单并完成收款';
    var cols=[['id','订单编号'],['petName','宠物'],['serviceType','服务'],['price','应收金额'],['createTime','创建时间'],['status','服务状态'],['paymentStatus','支付状态'],['paymentMethod','支付方式']];
    content.innerHTML='<div class="panel"><div class="toolbar"><div class="commercial-tools"><input id="checkoutSearch" class="search" placeholder="搜索订单 / 宠物 / 服务" oninput="filterCheckout()"><select id="payStatusFilter" onchange="filterCheckout()"><option value="">全部支付状态</option><option>待支付</option><option>已支付</option></select><button id="exportCheckout" class="btn secondary">导出 Excel</button></div></div><div id="tableWrap"></div></div>';
    document.getElementById('exportCheckout').onclick=function(){exportExcel('收银结算',currentRows('checkout'),cols)};
    drawTable(cache,cols,'checkout');
  }
  window.filterCheckout=function(){
    var q=document.getElementById('checkoutSearch').value.toLowerCase(),st=document.getElementById('payStatusFilter').value;
    var rows=cache.filter(function(x){return (!q||JSON.stringify(x).toLowerCase().indexOf(q)>=0)&&(!st||x.paymentStatus===st)});
    drawTable(rows,[['id','订单编号'],['petName','宠物'],['serviceType','服务'],['price','应收金额'],['createTime','创建时间'],['status','服务状态'],['paymentStatus','支付状态'],['paymentMethod','支付方式']],'checkout');
  };
  window.openCheckout=function(data){
    modal.classList.remove('hidden');modalTitle.textContent='订单收银';modalSub.textContent='订单 '+data.id+' · 应收 ¥'+Number(data.price||0).toFixed(2);
    var member='<div class="field"><label>关联会员（可选）</label><select data-k="memberId"><option value="">不关联会员</option>';
    commercialMembers.forEach(function(m){member+='<option value="'+esc(m.id)+'">'+esc(m.customerName)+' · '+esc(m.level)+' · 余额¥'+Number(m.balance||0).toFixed(2)+'</option>'});
    member+='</select></div>';
    modalBody.innerHTML='<div class="grid2"><div class="field"><label>支付方式</label><select data-k="paymentMethod"><option>微信</option><option>支付宝</option><option>现金</option><option>银行卡</option><option>会员余额</option></select></div>'+member+'</div>';
    modalSave.textContent='确认收款';
    modalSave.onclick=async function(){var d=getData();if(d.paymentMethod==='会员余额'&&!d.memberId){alert('使用会员余额时请选择会员');return}await api('/api/orders/'+encodeURIComponent(data.id)+'/checkout',{method:'POST',body:JSON.stringify(d)});closeModal();modalSave.textContent='保存';await go('checkout')};
  };

  function renderLogTable(){
    pageTitle.textContent='操作日志';pageSub.textContent='查看关键新增、修改、删除和结算操作';
    var cols=[['id','序号'],['actor','操作人'],['method','方法'],['path','接口'],['statusCode','结果码'],['createdAt','时间']];
    content.innerHTML='<div class="panel"><div class="toolbar"><div class="commercial-tools"><input id="logSearch" class="search" placeholder="搜索操作人 / 接口" oninput="filterLogs()"><button id="exportLogs" class="btn secondary">导出 Excel</button></div></div><div id="tableWrap"></div></div>';
    document.getElementById('exportLogs').onclick=function(){exportExcel('操作日志',currentRows('logs'),cols)};
    drawTable(cache,cols,'logs');
  }
  window.filterLogs=function(){
    var q=document.getElementById('logSearch').value.toLowerCase();
    drawTable(cache.filter(function(x){return !q||JSON.stringify(x).toLowerCase().indexOf(q)>=0}),[['id','序号'],['actor','操作人'],['method','方法'],['path','接口'],['statusCode','结果码'],['createdAt','时间']],'logs');
  };

  var baseEditRow=editRow;
  editRow=function(kind,data){
    if(kind==='appointment') return openAppointment(data);
    if(kind==='member') return openMember(data);
    return baseEditRow(kind,data);
  };
  var baseRemoveRow=removeRow;
  removeRow=async function(kind,id){
    if(kind!=='appointment'&&kind!=='member') return baseRemoveRow(kind,id);
    if(!confirm('删除后无法恢复，确定要删除这条记录吗？')) return;
    var u=kind==='appointment'?'/api/appointments':'/api/members';
    await api(u+'/'+encodeURIComponent(id),{method:'DELETE'});await go(kind);
  };

  openOrder=function(data){
    data=data||null;modal.classList.remove('hidden');modalTitle.textContent=data?'编辑订单':'新增订单';modalSub.textContent=data?'修改订单信息':'订单编号保存时自动生成';
    var status='<div class="field"><label>状态</label><select data-k="status"><option '+(data&&data.status==='待完成'?'selected':'')+'>待完成</option><option '+(data&&data.status==='进行中'?'selected':'')+'>进行中</option><option '+(data&&data.status==='已完成'?'selected':'')+'>已完成</option></select></div>';
    modalBody.innerHTML='<div class="grid2"><div class="field"><label>宠物</label>'+petSelect(data?data.petId:'')+'</div>'+fld('服务类型','serviceType',data?data.serviceType:'','text','例如：美容')+fld('金额','price',data?data.price:'','number','例如：100')+(data?fld('创建时间','createTime',data.createTime||'','datetime-local'):'')+status+'</div>';
    modalSave.textContent='保存';
    modalSave.onclick=async function(){var d=getData();d.price=Number(d.price||0);if(data){d.id=data.id;await api('/api/orders/'+encodeURIComponent(data.id),{method:'PUT',body:JSON.stringify(d)})}else{await api('/api/orders/auto',{method:'POST',body:JSON.stringify(d)})}closeModal();await go('order')};
  };

  var oldRegister=registerUser;
  registerUser=async function(){
    loginMsg.textContent='';
    if(!username.value.trim()){loginMsg.style.color='#b91c1c';loginMsg.textContent='请输入用户名';return}
    if(password.value.length<8||!/[A-Za-z]/.test(password.value)||!/[0-9]/.test(password.value)){loginMsg.style.color='#b91c1c';loginMsg.textContent='密码至少8位，并且必须同时包含字母和数字';return}
    var phone=prompt('请输入注册手机号（8-15位数字，可带+号）');if(phone===null)return;
    if(!/^[+]?[0-9]{8,15}$/.test(phone.trim())){loginMsg.style.color='#b91c1c';loginMsg.textContent='手机号格式不正确';return}
    try{await api('/api/auth/register',{method:'POST',body:JSON.stringify({name:username.value.trim(),password:password.value,phone:phone.trim()})});loginMsg.style.color='#15803d';loginMsg.textContent='注册成功，请点击登录'}
    catch(e){loginMsg.style.color='#b91c1c';loginMsg.textContent=e.message}
  };

  doLogin=async function(){
    loginMsg.textContent='';
    if(!username.value.trim()||!password.value){loginMsg.style.color='#b91c1c';loginMsg.textContent='请输入用户名和密码';return}
    try{var r=await api('/api/auth/login',{method:'POST',body:JSON.stringify({name:username.value.trim(),password:password.value})});who.textContent=r.name;showApp();renderNav();await refreshPets();await go('dashboard')}
    catch(e){loginMsg.style.color='#b91c1c';loginMsg.textContent='登录失败：请检查用户名和密码'}
  };

  addNav();
})();
