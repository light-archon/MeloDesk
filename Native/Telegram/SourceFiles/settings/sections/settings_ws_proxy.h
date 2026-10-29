/*
MusiGram integration for Telegram Desktop.
Distributed under the Telegram Desktop GPL license; see LEGAL.
TG WS Proxy core is distributed separately under its MIT license.
*/
#pragma once

namespace Main { class Account; }
namespace Ui { class BoxContent; }

namespace Settings {

void StartBuiltInWsProxyIfEnabled();
object_ptr<Ui::BoxContent> BuiltInWsProxyBox(not_null<Main::Account*> account);

} // namespace Settings
