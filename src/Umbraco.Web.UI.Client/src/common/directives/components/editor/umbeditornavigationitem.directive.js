(function () {
    'use strict';
    
    function UmbEditorNavigationItemController($scope, $element, $attrs) {
        
        var vm = this;
        
        vm.clicked = function() {
            vm.onOpen({item:vm.item});
        };
        
        vm.anchorClicked = function(anchor, $event) {
            
            vm.onOpenAnchor({item:vm.item, anchor:anchor});
            $event.stopPropagation();
            $event.preventDefault();
        };
        
        var componentNode = $element[0];
        
        componentNode.classList.add('umb-sub-views-nav-item');
        
    }
    
    angular
        .module('umbraco.directives.html')
        .component('umbEditorNavigationItem', {
            templateUrl: 'views/components/editor/umb-editor-navigation-item.html',
            controller: UmbEditorNavigationItemController,
            controllerAs: 'vm',
            bindings: {
                item: '=',
                onOpen: '&',
                onOpenAnchor: '&',
                index: '@'
            }
        });

})();
