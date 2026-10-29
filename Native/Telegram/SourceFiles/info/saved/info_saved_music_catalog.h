#pragma once

#include <memory>
#include "base/object_ptr.h"

class QWidget;

namespace Ui {
class RpWidget;
} // namespace Ui

namespace Window {
class SectionMemento;
class SessionController;
} // namespace Window

namespace Info::Saved {

[[nodiscard]] std::shared_ptr<Window::SectionMemento> MakeCatalogMusic();
[[nodiscard]] object_ptr<Ui::RpWidget> MakePromotionCard(
	QWidget *parent,
	Fn<void(bool)> visibilityChanged);

[[nodiscard]] object_ptr<Ui::RpWidget> MakeCatalogBar(
	QWidget *parent,
	not_null<Window::SessionController*> controller,
	Fn<void(bool)> visibilityChanged);

} // namespace Info::Saved
